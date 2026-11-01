using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Jobs;
using Shared.MultiTenancy;
using Shared.Uow;
using Shared.Uow.Abstractions;
using Shared.Uow.Attributes;
using Shared.Uow.Events;
using Shared.Uow.Options;
using Xunit;

namespace Shared.BackgroundTasks.Tests;

public sealed class BackgroundJobExecutorUnitOfWorkTests
{
    [Fact]
    public async Task ExecuteAsync_AttributedJob_StartsAfterTenantActivation_AndCompletes()
    {
        var tenantId = Guid.NewGuid();
        var tenant = new TrackingCurrentTenant();
        var unitOfWorkManager = new TrackingUnitOfWorkManager(tenant);
        var probe = new JobProbe();
        using var services = new ServiceCollection()
            .AddSingleton(probe)
            .AddTransient(_ =>
            {
                probe.ResolvedInsideUnitOfWork = unitOfWorkManager.Started.Count == 1;
                return new ReadJob(probe);
            })
            .BuildServiceProvider();
        var executor = CreateExecutor<ReadJob, ReadArgs>(tenant, unitOfWorkManager, new ReadArgs(), "read-job");

        await executor.ExecuteAsync(CreateEnvelope("read-job", tenantId), services);

        var unitOfWork = Assert.Single(unitOfWorkManager.Started);
        Assert.Equal(tenantId, unitOfWorkManager.TenantAtBegin);
        Assert.True(unitOfWorkManager.LastRequiresNew);
        Assert.False(unitOfWork.Options.IsTransactional);
        Assert.True(unitOfWork.IsCompleted);
        Assert.True(unitOfWork.IsDisposed);
        Assert.True(probe.Executed);
        Assert.True(probe.ResolvedInsideUnitOfWork);
        Assert.Null(tenant.Id);
    }

    [Fact]
    public async Task ExecuteAsync_AttributedJobFailure_DisposesWithoutCompleting()
    {
        var tenant = new TrackingCurrentTenant();
        var unitOfWorkManager = new TrackingUnitOfWorkManager(tenant);
        using var services = new ServiceCollection()
            .AddTransient<FailingJob>()
            .BuildServiceProvider();
        var executor = CreateExecutor<FailingJob, FailingArgs>(
            tenant,
            unitOfWorkManager,
            new FailingArgs(),
            "failing-job");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(CreateEnvelope("failing-job", Guid.NewGuid()), services));

        var unitOfWork = Assert.Single(unitOfWorkManager.Started);
        Assert.True(unitOfWork.Options.IsTransactional);
        Assert.False(unitOfWork.IsCompleted);
        Assert.True(unitOfWork.IsDisposed);
    }

    [Fact]
    public async Task ExecuteAsync_UnattributedJob_DoesNotCreateUnitOfWork()
    {
        var tenant = new TrackingCurrentTenant();
        var unitOfWorkManager = new TrackingUnitOfWorkManager(tenant);
        var probe = new JobProbe();
        using var services = new ServiceCollection()
            .AddSingleton(probe)
            .AddTransient<PlainJob>()
            .BuildServiceProvider();
        var executor = CreateExecutor<PlainJob, PlainArgs>(
            tenant,
            unitOfWorkManager,
            new PlainArgs(),
            "plain-job");

        await executor.ExecuteAsync(CreateEnvelope("plain-job", tenantId: null), services);

        Assert.Empty(unitOfWorkManager.Started);
        Assert.True(probe.Executed);
    }

    private static BackgroundJobExecutor CreateExecutor<TJob, TArgs>(
        ICurrentTenant tenant,
        IUnitOfWorkManager unitOfWorkManager,
        TArgs args,
        string jobName)
        where TJob : IBackgroundJob<TArgs>
    {
        var options = new BackgroundTasksOptions();
        options.Jobs.Add(BackgroundJobDefinition.Create<TJob, TArgs>(jobName));
        return new BackgroundJobExecutor(
            new BackgroundJobRegistry(Options.Create(options)),
            new FixedSerializer(args!),
            tenant,
            unitOfWorkManager,
            Options.Create(new UnitOfWorkDefaultOptions()));
    }

    private static BackgroundJobEnvelope CreateEnvelope(string jobName, Guid? tenantId)
    {
        return new BackgroundJobEnvelope
        {
            JobName = jobName,
            ArgsJson = "{}",
            TenantId = tenantId,
            EnqueuedAtUtc = DateTime.UtcNow,
        };
    }

    private sealed record ReadArgs;
    private sealed record FailingArgs;
    private sealed record PlainArgs;

    private sealed class JobProbe
    {
        public bool Executed { get; set; }
        public bool ResolvedInsideUnitOfWork { get; set; }
    }

    private sealed class ReadJob(JobProbe probe) : IBackgroundJob<ReadArgs>
    {
        [UnitOfWork(isTransactional: false)]
        public Task ExecuteAsync(BackgroundJobExecutionContext<ReadArgs> context)
        {
            probe.Executed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingJob : IBackgroundJob<FailingArgs>
    {
        [UnitOfWork(isTransactional: true)]
        public Task ExecuteAsync(BackgroundJobExecutionContext<FailingArgs> context)
        {
            throw new InvalidOperationException("Expected test failure.");
        }
    }

    private sealed class PlainJob(JobProbe probe) : IBackgroundJob<PlainArgs>
    {
        public Task ExecuteAsync(BackgroundJobExecutionContext<PlainArgs> context)
        {
            probe.Executed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedSerializer(object args) : IBackgroundJobSerializer
    {
        public string SerializeArgs<TArgs>(TArgs value) => "{}";

        public object DeserializeArgs(string json, Type argsType) => args;
    }

    private sealed class TrackingCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name => null;

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previous = Id;
            Id = id;
            return new CallbackDisposable(() => Id = previous);
        }
    }

    private sealed class TrackingUnitOfWorkManager(ICurrentTenant currentTenant) : IUnitOfWorkManager
    {
        public List<TrackingUnitOfWork> Started { get; } = [];
        public Guid? TenantAtBegin { get; private set; }
        public bool LastRequiresNew { get; private set; }
        public IUnitOfWork? Current => null;

        public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false)
        {
            TenantAtBegin = currentTenant.Id;
            LastRequiresNew = requiresNew;
            var unitOfWork = new TrackingUnitOfWork(options);
            Started.Add(unitOfWork);
            return unitOfWork;
        }

        public IUnitOfWork Begin(bool requiresNew = false) => Begin(new UnitOfWorkOptions(), requiresNew);
        public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => Begin(requiresNew);
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) => throw new NotSupportedException();
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => false;
    }

    private sealed class TrackingUnitOfWork(UnitOfWorkOptions options) : IUnitOfWork
    {
        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
        public event EventHandler<UnitOfWorkFailedEventArgs>? Failed
        {
            add { }
            remove { }
        }
        public event EventHandler<UnitOfWorkEventArgs>? Disposed;
        public UnitOfWorkOptions Options { get; } = options;
        public IUnitOfWork? Outer => null;
        public bool IsReserved => false;
        public bool IsDisposed { get; private set; }
        public bool IsCompleted { get; private set; }
        public string? ReservationName => null;
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public void SetOuter(IUnitOfWork? outer) { }
        public void Initialize(UnitOfWorkOptions value) { }
        public void Reserve(string reservationName) { }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            IsCompleted = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void OnCompleted(Func<Task> handler) { }
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

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}
