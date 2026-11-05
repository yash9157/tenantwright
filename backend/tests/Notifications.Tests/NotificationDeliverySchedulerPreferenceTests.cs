using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Permissions.Application.Contracts.Features;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.EventContracts;
using Shared.Uow;
using Shared.Uow.Abstractions;
using Shared.Uow.Events;
using Shared.Uow.Options;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Query;
using Shared.BackgroundTasks.Workers;
using Xunit;

namespace Notifications.Tests;

public class NotificationDeliverySchedulerPreferenceTests
{
    [Fact]
    public async Task ScheduleAsync_should_skip_optional_notification_when_preference_is_disabled()
    {
        var wakeSignal = new CapturingWakeSignal();
        var scheduler = new NotificationDeliveryScheduler(
            new NoopUnitOfWorkManager(),
            new ImmediateUnitOfWorkExecutor(),
            new EmptyRepository<Notification, Guid>(),
            new EmptyRepository<NotificationRecipient, long>(),
            new EmptyRepository<NotificationDelivery, long>(),
            Options.Create(new NotificationsChannelsOptions
            {
                EmailEnabled = true
            }),
            Options.Create(new NotificationsDeliveryOptions()),
            new AlwaysEnabledFeatureCheckerClient(),
            new DisabledPreferenceClient(),
            wakeSignal,
            new PassThroughRateLimiter(),
            new PassThroughAudienceResolver(),
            new NeverSuppressedTenantNotificationChecker(),
            new NoopNotificationDigestPlanner(),
            NullLogger<NotificationDeliveryScheduler>.Instance);

        var result = await scheduler.ScheduleAsync(new NotificationChannelRequestTenant
        {
            RequestId = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            SourceService = "notifications-tests",
            Channel = NotificationSendChannel.Email,
            TemplateName = "Identity.SecurityRiskBlockedAlert",
            RespectUserPreferences = true,
            PreferenceUserId = Guid.CreateVersion7(),
            PreferenceKey = "risk-block-alert",
            Recipients =
            [
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.EmailAddress,
                    RecipientValue = "user@test.local"
                }
            ]
        });

        Assert.True(result.Accepted);
        Assert.True(result.SkippedByPreferences);
        Assert.False(result.IsDuplicate);
        Assert.Equal(Guid.Empty, result.NotificationId);
        Assert.Equal(0, result.QueuedCount);
        Assert.Equal(0, wakeSignal.SignalCount);
    }

    [Fact]
    public async Task ScheduleAsync_should_signal_after_successful_delivery_rows_commit()
    {
        var wakeSignal = new CapturingWakeSignal();
        var notificationRepository = new CapturingRepository<Notification, Guid>();
        var recipientRepository = new CapturingRepository<NotificationRecipient, long>();
        var deliveryRepository = new CapturingRepository<NotificationDelivery, long>();
        var scheduler = new NotificationDeliveryScheduler(
            new NoopUnitOfWorkManager(),
            new ImmediateUnitOfWorkExecutor(),
            notificationRepository,
            recipientRepository,
            deliveryRepository,
            Options.Create(new NotificationsChannelsOptions
            {
                EmailEnabled = true
            }),
            Options.Create(new NotificationsDeliveryOptions()),
            new AlwaysEnabledFeatureCheckerClient(),
            new EnabledPreferenceClient(),
            wakeSignal,
            new PassThroughRateLimiter(),
            new PassThroughAudienceResolver(),
            new NeverSuppressedTenantNotificationChecker(),
            new NoopNotificationDigestPlanner(),
            NullLogger<NotificationDeliveryScheduler>.Instance);

        var result = await scheduler.ScheduleAsync(CreateEmailEvent());

        Assert.True(result.Accepted);
        Assert.False(result.IsDuplicate);
        Assert.Equal(1, result.QueuedCount);
        Assert.Single(notificationRepository.Items);
        Assert.Single(recipientRepository.Items);
        Assert.Single(deliveryRepository.Items);
        Assert.Equal(1, wakeSignal.SignalCount);
        Assert.Equal(NotificationDeliveryWakeKeys.Delivery, wakeSignal.LastKey);
        Assert.Equal("tenantwright:notifications:delivery:wakeup", wakeSignal.LastOptions?.ValkeyChannel);
    }

    [Fact]
    public async Task ScheduleAsync_group_recipient_should_join_the_ambient_unit_of_work_and_signal_after_it_commits()
    {
        var unitOfWorkManager = new AmbientRecordingUnitOfWorkManager();
        var unitOfWorkExecutor = new RejectingUnitOfWorkExecutor();
        var wakeSignal = new CapturingWakeSignal();
        var scheduler = new NotificationDeliveryScheduler(
            unitOfWorkManager,
            unitOfWorkExecutor,
            new CapturingRepository<Notification, Guid>(),
            new CapturingRepository<NotificationRecipient, long>(),
            new CapturingRepository<NotificationDelivery, long>(),
            Options.Create(new NotificationsChannelsOptions { EmailEnabled = true }),
            Options.Create(new NotificationsDeliveryOptions()),
            new AlwaysEnabledFeatureCheckerClient(),
            new EnabledPreferenceClient(),
            wakeSignal,
            new PassThroughRateLimiter(),
            new FixedEmailAudienceResolver(),
            new NeverSuppressedTenantNotificationChecker(),
            new NoopNotificationDigestPlanner(),
            NullLogger<NotificationDeliveryScheduler>.Instance);

        var result = await scheduler.ScheduleAsync(new NotificationChannelRequestTenant
        {
            RequestId = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            SourceService = "notifications-tests",
            Channel = NotificationSendChannel.Email,
            TemplateName = "Identity.SecurityRiskBlockedAlert",
            Recipients =
            [
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.Role,
                    RecipientValue = "tenant-admin"
                }
            ]
        });

        Assert.True(result.Accepted);
        Assert.False(result.IsDuplicate);
        Assert.Equal(1, result.QueuedCount);
        // The scheduler joined the ambient owner (the inbox transaction in production) rather than committing its own
        // transaction or starting an independent executor scope.
        Assert.Equal(0, unitOfWorkExecutor.CallCount);
        Assert.False(unitOfWorkManager.Ambient.IsCompleted);
        Assert.Equal(1, unitOfWorkManager.Ambient.CompletedHandlerCount);
        Assert.Equal(0, wakeSignal.SignalCount);

        await unitOfWorkManager.Ambient.CompleteAsync();

        Assert.Equal(1, wakeSignal.SignalCount);
    }

    [Fact]
    public async Task ScheduleAsync_should_persist_a_future_not_before_time_for_durable_delivery()
    {
        var deliveryRepository = new CapturingRepository<NotificationDelivery, long>();
        var scheduledForUtc = DateTime.UtcNow.AddHours(2);
        var scheduler = new NotificationDeliveryScheduler(
            new NoopUnitOfWorkManager(),
            new ImmediateUnitOfWorkExecutor(),
            new CapturingRepository<Notification, Guid>(),
            new CapturingRepository<NotificationRecipient, long>(),
            deliveryRepository,
            Options.Create(new NotificationsChannelsOptions { EmailEnabled = true }),
            Options.Create(new NotificationsDeliveryOptions()),
            new AlwaysEnabledFeatureCheckerClient(),
            new EnabledPreferenceClient(),
            new CapturingWakeSignal(),
            new PassThroughRateLimiter(),
            new PassThroughAudienceResolver(),
            new NeverSuppressedTenantNotificationChecker(),
            new NoopNotificationDigestPlanner(),
            NullLogger<NotificationDeliveryScheduler>.Instance);

        var result = await scheduler.ScheduleAtAsync(
            CreateEmailEvent(),
            scheduledForUtc);

        Assert.True(result.Accepted);
        Assert.Equal(scheduledForUtc, result.ScheduledForUtc);
        Assert.Single(deliveryRepository.Items);
        Assert.Equal(scheduledForUtc, deliveryRepository.Items[0].NextAttemptTimeUtc);
    }

    [Fact]
    public async Task ScheduleAsync_should_reject_a_schedule_time_outside_the_configured_horizon()
    {
        var scheduler = new NotificationDeliveryScheduler(
            new NoopUnitOfWorkManager(),
            new ImmediateUnitOfWorkExecutor(),
            new CapturingRepository<Notification, Guid>(),
            new CapturingRepository<NotificationRecipient, long>(),
            new CapturingRepository<NotificationDelivery, long>(),
            Options.Create(new NotificationsChannelsOptions { EmailEnabled = true }),
            Options.Create(new NotificationsDeliveryOptions { MaxScheduleHorizonDays = 1 }),
            new AlwaysEnabledFeatureCheckerClient(),
            new EnabledPreferenceClient(),
            new CapturingWakeSignal(),
            new PassThroughRateLimiter(),
            new PassThroughAudienceResolver(),
            new NeverSuppressedTenantNotificationChecker(),
            new NoopNotificationDigestPlanner(),
            NullLogger<NotificationDeliveryScheduler>.Instance);

        var exception = await Assert.ThrowsAsync<Shared.SharedValidationException>(() => scheduler.ScheduleAtAsync(
            CreateEmailEvent(),
            DateTime.UtcNow.AddDays(2)));

        Assert.Equal(NotificationErrorCodes.ScheduleHorizonExceeded, exception.Code);
        Assert.Equal("Scheduled delivery time exceeds the configured scheduling horizon.", exception.Message);
    }

    [Fact]
    public async Task ScheduleAsync_should_not_signal_duplicate_delivery_request()
    {
        var requestId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var wakeSignal = new CapturingWakeSignal();
        var notificationRepository = new CapturingRepository<Notification, Guid>();
        notificationRepository.Items.Add(new Notification(
            id: Guid.CreateVersion7(),
            tenantId: tenantId,
            requestId: requestId,
            channel: NotificationChannel.Email,
            templateName: "Identity.SecurityRiskBlockedAlert",
            templateModelJson: "{}",
            subject: null));
        var scheduler = new NotificationDeliveryScheduler(
            new NoopUnitOfWorkManager(),
            new ImmediateUnitOfWorkExecutor(),
            notificationRepository,
            new CapturingRepository<NotificationRecipient, long>(),
            new CapturingRepository<NotificationDelivery, long>(),
            Options.Create(new NotificationsChannelsOptions
            {
                EmailEnabled = true
            }),
            Options.Create(new NotificationsDeliveryOptions()),
            new AlwaysEnabledFeatureCheckerClient(),
            new EnabledPreferenceClient(),
            wakeSignal,
            new PassThroughRateLimiter(),
            new PassThroughAudienceResolver(),
            new NeverSuppressedTenantNotificationChecker(),
            new NoopNotificationDigestPlanner(),
            NullLogger<NotificationDeliveryScheduler>.Instance);

        var result = await scheduler.ScheduleAsync(CreateEmailEvent(requestId, tenantId));

        Assert.True(result.Accepted);
        Assert.True(result.IsDuplicate);
        Assert.Equal(0, result.QueuedCount);
        Assert.Equal(0, wakeSignal.SignalCount);
    }

    private static NotificationChannelRequestTenant CreateEmailEvent(
        Guid? requestId = null,
        Guid? tenantId = null)
    {
        return new NotificationChannelRequestTenant
        {
            RequestId = requestId ?? Guid.CreateVersion7(),
            TenantId = tenantId ?? Guid.CreateVersion7(),
            SourceService = "notifications-tests",
            Channel = NotificationSendChannel.Email,
            TemplateName = "Identity.SecurityRiskBlockedAlert",
            Recipients =
            [
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.EmailAddress,
                    RecipientValue = "user@test.local"
                }
            ]
        };
    }

    private sealed class DisabledPreferenceClient : IIdentityNotificationPreferenceClient
    {
        public Task<bool> IsNotificationEnabledAsync(Guid? tenantId, Guid userId, string preferenceKey, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class EnabledPreferenceClient : IIdentityNotificationPreferenceClient
    {
        public Task<bool> IsNotificationEnabledAsync(Guid? tenantId, Guid userId, string preferenceKey, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class AlwaysEnabledFeatureCheckerClient : IFeatureCheckerClient
    {
        public Task<bool> IsEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<Dictionary<string, bool>> AreEnabledAsync(Guid? tenantId, IReadOnlyCollection<string> featureNames, CancellationToken cancellationToken = default)
            => Task.FromResult(featureNames.ToDictionary(x => x, _ => true, StringComparer.Ordinal));
    }

    private sealed class EmptyRepository<TEntity, TKey> : IRepository<TEntity, TKey>
        where TEntity : class, IEntity<TKey>
    {
        public string? EntityName { get; set; }
        public string ProviderName => "InMemory";

        public Task<IQueryable<TEntity>> WithDetailsAsync() => Task.FromResult<IQueryable<TEntity>>(new TestAsyncEnumerable<TEntity>([]));
        public Task<IQueryable<TEntity>> WithDetailsAsync(params Expression<Func<TEntity, object>>[] propertySelectors) => Task.FromResult<IQueryable<TEntity>>(new TestAsyncEnumerable<TEntity>([]));
        public Task<IQueryable<TEntity>> GetQueryableAsync() => Task.FromResult<IQueryable<TEntity>>(new TestAsyncEnumerable<TEntity>([]));
        public Task<List<TEntity>> GetListAsync(bool includeDetails = false, CancellationToken cancellationToken = default) => Task.FromResult(new List<TEntity>());
        public Task<long> GetCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<List<TEntity>> GetPagedListAsync(int skipCount, int maxResultCount, string sorting, bool includeDetails = false, CancellationToken cancellationToken = default) => Task.FromResult(new List<TEntity>());
        public Task<TEntity?> FindAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default) => Task.FromResult<TEntity?>(null);
        public Task<TEntity> GetAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<TEntity>> GetListAsync(Expression<Func<TEntity, bool>> predicate, bool includeDetails = false, CancellationToken cancellationToken = default) => Task.FromResult(new List<TEntity>());
        public Task<TEntity?> FindAsync(Expression<Func<TEntity, bool>> predicate, bool includeDetails = true, CancellationToken cancellationToken = default) => Task.FromResult<TEntity?>(null);
        public Task<TEntity> GetAsync(Expression<Func<TEntity, bool>> predicate, bool includeDetails = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TEntity> InsertAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default) => Task.FromResult(entity);
        public Task InsertManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TEntity> UpdateAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default) => Task.FromResult(entity);
        public Task UpdateManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(TKey id, bool autoSave = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteManyAsync(IEnumerable<TKey> ids, bool autoSave = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Expression<Func<TEntity, bool>> predicate, bool autoSave = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteDirectAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CapturingRepository<TEntity, TKey> : IRepository<TEntity, TKey>
        where TEntity : class, IEntity<TKey>
    {
        public List<TEntity> Items { get; } = [];
        public string? EntityName { get; set; }
        public string ProviderName => "InMemory";

        public Task<IQueryable<TEntity>> WithDetailsAsync() => Task.FromResult<IQueryable<TEntity>>(new TestAsyncEnumerable<TEntity>(Items));
        public Task<IQueryable<TEntity>> WithDetailsAsync(params Expression<Func<TEntity, object>>[] propertySelectors) => Task.FromResult<IQueryable<TEntity>>(new TestAsyncEnumerable<TEntity>(Items));
        public Task<IQueryable<TEntity>> GetQueryableAsync() => Task.FromResult<IQueryable<TEntity>>(new TestAsyncEnumerable<TEntity>(Items));
        public Task<List<TEntity>> GetListAsync(bool includeDetails = false, CancellationToken cancellationToken = default) => Task.FromResult(Items.ToList());
        public Task<long> GetCountAsync(CancellationToken cancellationToken = default) => Task.FromResult((long)Items.Count);
        public Task<List<TEntity>> GetPagedListAsync(int skipCount, int maxResultCount, string sorting, bool includeDetails = false, CancellationToken cancellationToken = default) => Task.FromResult(Items.Skip(skipCount).Take(maxResultCount).ToList());
        public Task<TEntity?> FindAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default) => Task.FromResult(Items.FirstOrDefault(x => EqualityComparer<TKey>.Default.Equals(x.Id, id)));
        public Task<TEntity> GetAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default) => Task.FromResult(Items.First(x => EqualityComparer<TKey>.Default.Equals(x.Id, id)));
        public Task<List<TEntity>> GetListAsync(Expression<Func<TEntity, bool>> predicate, bool includeDetails = false, CancellationToken cancellationToken = default) => Task.FromResult(Items.AsQueryable().Where(predicate).ToList());
        public Task<TEntity?> FindAsync(Expression<Func<TEntity, bool>> predicate, bool includeDetails = true, CancellationToken cancellationToken = default) => Task.FromResult(Items.AsQueryable().FirstOrDefault(predicate));
        public Task<TEntity> GetAsync(Expression<Func<TEntity, bool>> predicate, bool includeDetails = true, CancellationToken cancellationToken = default) => Task.FromResult(Items.AsQueryable().First(predicate));
        public Task<TEntity> InsertAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            Items.Add(entity);
            return Task.FromResult(entity);
        }

        public Task InsertManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            Items.AddRange(entities);
            return Task.CompletedTask;
        }

        public Task<TEntity> UpdateAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default) => Task.FromResult(entity);
        public Task UpdateManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            Items.Remove(entity);
            return Task.CompletedTask;
        }

        public Task DeleteManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            foreach (var entity in entities)
            {
                Items.Remove(entity);
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(TKey id, bool autoSave = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteManyAsync(IEnumerable<TKey> ids, bool autoSave = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Expression<Func<TEntity, bool>> predicate, bool autoSave = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteDirectAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CapturingWakeSignal : IBackgroundWorkerWakeSignal
    {
        public int SignalCount { get; private set; }
        public string? LastKey { get; private set; }
        public BackgroundWorkerWakeOptions? LastOptions { get; private set; }

        public Task SignalAsync(
            string key,
            BackgroundWorkerWakeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            SignalCount++;
            LastKey = key;
            LastOptions = options;
            return Task.CompletedTask;
        }

        public Task<BackgroundWorkerWakeResult> WaitAsync(
            string key,
            BackgroundWorkerWakeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BackgroundWorkerWakeResult.SafetyPollTimeout);
        }
    }

    private sealed class TestAsyncQueryProvider<TEntity> : IAsyncQueryProvider
    {
        private readonly IQueryProvider _inner;

        public TestAsyncQueryProvider(IQueryProvider inner)
        {
            _inner = inner;
        }

        public IQueryable CreateQuery(Expression expression)
        {
            return new TestAsyncEnumerable<TEntity>(expression);
        }

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        {
            return new TestAsyncEnumerable<TElement>(expression);
        }

        public object? Execute(Expression expression)
        {
            return _inner.Execute(expression);
        }

        public TResult Execute<TResult>(Expression expression)
        {
            return _inner.Execute<TResult>(expression);
        }

        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var expectedResultType = typeof(TResult).GetGenericArguments()[0];
            var executionResult = typeof(IQueryProvider)
                .GetMethod(nameof(IQueryProvider.Execute), 1, [typeof(Expression)])!
                .MakeGenericMethod(expectedResultType)
                .Invoke(_inner, [expression]);
            return (TResult)typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(expectedResultType)
                .Invoke(null, [executionResult])!;
        }
    }

    private sealed class TestAsyncEnumerable<TEntity> : EnumerableQuery<TEntity>, IAsyncEnumerable<TEntity>, IQueryable<TEntity>
    {
        public TestAsyncEnumerable(IEnumerable<TEntity> enumerable)
            : base(enumerable)
        {
        }

        public TestAsyncEnumerable(Expression expression)
            : base(expression)
        {
        }

        public IAsyncEnumerator<TEntity> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new TestAsyncEnumerator<TEntity>(this.AsEnumerable().GetEnumerator());
        }

        IQueryProvider IQueryable.Provider => new TestAsyncQueryProvider<TEntity>(this);
    }

    private sealed class TestAsyncEnumerator<TEntity> : IAsyncEnumerator<TEntity>
    {
        private readonly IEnumerator<TEntity> _inner;

        public TestAsyncEnumerator(IEnumerator<TEntity> inner)
        {
            _inner = inner;
        }

        public TEntity Current => _inner.Current;

        public ValueTask DisposeAsync()
        {
            _inner.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> MoveNextAsync()
        {
            return ValueTask.FromResult(_inner.MoveNext());
        }
    }

    private sealed class NoopUnitOfWorkManager : IUnitOfWorkManager
    {
        public IUnitOfWork? Current => null;
        public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => new NoopUnitOfWork(options);
        public IUnitOfWork Begin(bool requiresNew = false) => new NoopUnitOfWork(new UnitOfWorkOptions());
        public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => new NoopUnitOfWork(new UnitOfWorkOptions());
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) { }
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => true;
    }

    private sealed class NoopUnitOfWork : IUnitOfWork
    {
        private readonly List<Func<Task>> _completionHandlers = [];

        public NoopUnitOfWork(UnitOfWorkOptions options)
        {
            Options = options;
        }

        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public event EventHandler<UnitOfWorkFailedEventArgs>? Failed { add { } remove { } }
        public event EventHandler<UnitOfWorkEventArgs>? Disposed;
        public UnitOfWorkOptions Options { get; }
        public IUnitOfWork? Outer => null;
        public bool IsReserved => false;
        public bool IsDisposed { get; private set; }
        public bool IsCompleted { get; private set; }
        public string? ReservationName => null;
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public void SetOuter(IUnitOfWork? outer) { }
        public void Initialize(UnitOfWorkOptions options) { }
        public void Reserve(string reservationName) { }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            IsCompleted = true;
            foreach (var handler in _completionHandlers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await handler();
            }
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void OnCompleted(Func<Task> handler) => _completionHandlers.Add(handler);
        public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory) => factory(key);
        public IDatabaseApi? FindDatabaseApi(string key) => null;
        public void AddDatabaseApi(string key, IDatabaseApi api) { }
        public IDatabaseApi GetOrAddDatabaseApi(string key, Func<IDatabaseApi> factory) => factory();
        public ITransactionApi? FindTransactionApi(string key) => null;
        public void AddTransactionApi(string key, ITransactionApi api) { }
        public ITransactionApi GetOrAddTransactionApi(string key, Func<ITransactionApi> factory) => factory();
        public void Dispose()
        {
            IsDisposed = true;
            Disposed?.Invoke(this, new UnitOfWorkEventArgs(this));
        }
    }
}
