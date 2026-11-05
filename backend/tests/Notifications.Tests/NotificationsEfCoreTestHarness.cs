using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.AuditLogs.Auditing;
using Shared.Data.Filtering;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore;
using Shared.MultiTenancy;
using Shared.Security;
using Shared.Uow;
using Shared.Uow.Abstractions;
using Shared.Uow.Events;
using Shared.Uow.Options;
using System.Linq.Expressions;
using System.Security.Claims;

namespace Notifications.Tests;

/// <summary>
/// Shared harness for building a real <see cref="NotificationsDbContextApp"/> over the EF Core
/// InMemory provider with the shared multi-tenant infrastructure wired.
/// <para>
/// Set <c>multiTenantFilterEnabled: true</c> together with <c>tenantId: null</c> to reproduce the
/// production tenant-filter path that runs from a distributed-event handler with no ambient tenant
/// context (the global filter collapses to <c>TenantId == null</c>). Registering a disabled data
/// filter (the default) turns the tenant query filter off and would let tenant-scoped tests pass
/// for the wrong reason.
/// </para>
/// </summary>
internal static class NotificationsEfCoreTestHarness
{
    public static NotificationsDbContextApp BuildDbContext(
        string databaseName,
        Guid? tenantId,
        bool multiTenantFilterEnabled = false,
        InMemoryDatabaseRoot? databaseRoot = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<NotificationsDbContextApp>();
        if (databaseRoot != null)
        {
            optionsBuilder.UseInMemoryDatabase(databaseName, databaseRoot);
        }
        else
        {
            optionsBuilder.UseInMemoryDatabase(databaseName);
        }

        var dbContext = new NotificationsDbContextApp(optionsBuilder.Options)
        {
            ServiceProvider = CreateServiceProvider(tenantId, multiTenantFilterEnabled)
        };
        dbContext.Initialize(new EfCoreDbContextInitializationContext(new NoopUnitOfWork(new UnitOfWorkOptions())));
        return dbContext;
    }

    /// <summary>
    /// Builds a context over the SQLite provider (relational), which — unlike the InMemory provider —
    /// supports <c>ExecuteDelete</c> and enforces FK <c>ON DELETE CASCADE</c>. Open the connection
    /// (with <c>Foreign Keys=True</c>) and call <c>EnsureCreated()</c> once before use; keep it open
    /// for the lifetime of the in-memory database.
    /// </summary>
    public static NotificationsDbContextApp BuildSqliteDbContext(
        SqliteConnection connection,
        Guid? tenantId,
        bool multiTenantFilterEnabled = false)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContextApp>()
            .UseSqlite(connection)
            .Options;

        var dbContext = new NotificationsDbContextApp(options)
        {
            ServiceProvider = CreateServiceProvider(tenantId, multiTenantFilterEnabled)
        };
        dbContext.Initialize(new EfCoreDbContextInitializationContext(new NoopUnitOfWork(new UnitOfWorkOptions())));
        return dbContext;
    }

    public static NotificationsDbContextApp BuildPostgresDbContext(
        string connectionString, Guid? tenantId, bool multiTenantFilterEnabled = false)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContextApp>().UseNpgsql(connectionString).Options;
        var dbContext = new NotificationsDbContextApp(options)
        {
            ServiceProvider = CreateServiceProvider(tenantId, multiTenantFilterEnabled)
        };
        dbContext.Initialize(new EfCoreDbContextInitializationContext(new NoopUnitOfWork(new UnitOfWorkOptions())));
        return dbContext;
    }

    public static IDbContextProvider<NotificationsDbContextApp> AsDbContextProvider(NotificationsDbContextApp dbContext)
        => new TestDbContextProvider(dbContext);

    private sealed class TestDbContextProvider(NotificationsDbContextApp dbContext)
        : IDbContextProvider<NotificationsDbContextApp>
    {
        public Task<NotificationsDbContextApp> GetDbContextAsync()
            => Task.FromResult(dbContext);
    }

    private static ServiceProvider CreateServiceProvider(Guid? tenantId, bool multiTenantFilterEnabled)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentTenant>(new TestCurrentTenant(tenantId));
        services.AddSingleton<IDataFilter>(new ConfigurableDataFilter(multiTenantFilterEnabled));
        services.AddSingleton<IAuditPropertySetter>(new NoopAuditPropertySetter());
        services.AddSingleton<IAuditingManager>(new NoopAuditingManager());
        services.AddSingleton<IUnitOfWorkManager>(new NoopUnitOfWorkManager());
        services.AddSingleton<ICurrentPrincipalAccessor>(new TestCurrentPrincipalAccessor("u1"));
        return services.BuildServiceProvider();
    }
}

internal sealed class RelationalUnitOfWorkExecutor(NotificationsDbContextApp dbContext) : IUnitOfWorkExecutor
{
    public async Task ExecuteTransactionalAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await operation(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<TResult> ExecuteTransactionalAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var result = await operation(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task ExecuteNonTransactionalAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
        => operation(cancellationToken);

    public Task<TResult> ExecuteNonTransactionalAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        => operation(cancellationToken);
}

internal sealed class ImmediateUnitOfWorkExecutor : IUnitOfWorkExecutor
{
    public Task ExecuteTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        => operation(cancellationToken);

    public Task<TResult> ExecuteTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        => operation(cancellationToken);

    public Task ExecuteNonTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        => operation(cancellationToken);

    public Task<TResult> ExecuteNonTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        => operation(cancellationToken);
}

internal sealed class RejectingUnitOfWorkExecutor : IUnitOfWorkExecutor
{
    public int CallCount { get; private set; }

    public Task ExecuteTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        => Reject<Task>();

    public Task<TResult> ExecuteTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        => Reject<Task<TResult>>();

    public Task ExecuteNonTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        => Reject<Task>();

    public Task<TResult> ExecuteNonTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        => Reject<Task<TResult>>();

    private TResult Reject<TResult>()
    {
        CallCount++;
        throw new InvalidOperationException("Scheduling inside an ambient owner must not start an independent unit of work.");
    }
}

/// <summary>
/// Unit-of-work manager with one ambient, transactional owner (as the messaging inbox transaction provides in
/// production). Joining callers receive a real <see cref="Shared.Uow.Implementations.ChildUnitOfWork"/> over it.
/// </summary>
internal sealed class AmbientRecordingUnitOfWorkManager : IUnitOfWorkManager
{
    public RecordingUnitOfWork Ambient { get; } = new(new UnitOfWorkOptions { IsTransactional = true });
    public IUnitOfWork? Current => Ambient;

    public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) =>
        requiresNew
            ? throw new InvalidOperationException("The ambient owner must be joined, not replaced.")
            : new Shared.Uow.Implementations.ChildUnitOfWork(Ambient);

    public IUnitOfWork Begin(bool requiresNew = false) => Begin(new UnitOfWorkOptions(), requiresNew);

    public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) =>
        throw new InvalidOperationException("Reservation is not used by this test.");

    public void BeginReserved(string reservationName, UnitOfWorkOptions options) =>
        throw new InvalidOperationException("Reservation is not used by this test.");

    public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => false;
}

/// <summary>A unit of work that records its completion handlers and runs them on completion, like the real one.</summary>
internal sealed class RecordingUnitOfWork(UnitOfWorkOptions options) : IUnitOfWork
{
    private readonly List<Func<Task>> _completedHandlers = [];

    public int CompletedHandlerCount => _completedHandlers.Count;
    public Guid Id { get; } = Guid.NewGuid();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public event EventHandler<UnitOfWorkFailedEventArgs>? Failed { add { } remove { } }
    public event EventHandler<UnitOfWorkEventArgs>? Disposed { add { } remove { } }
    public UnitOfWorkOptions Options { get; } = options;
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
        foreach (var handler in _completedHandlers.ToArray())
        {
            await handler();
        }
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void OnCompleted(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _completedHandlers.Add(handler);
    }

    public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory) => factory(key);
    public IDatabaseApi? FindDatabaseApi(string key) => null;
    public void AddDatabaseApi(string key, IDatabaseApi api) { }
    public IDatabaseApi GetOrAddDatabaseApi(string key, Func<IDatabaseApi> factory) => factory();
    public ITransactionApi? FindTransactionApi(string key) => null;
    public void AddTransactionApi(string key, ITransactionApi api) { }
    public ITransactionApi GetOrAddTransactionApi(string key, Func<ITransactionApi> factory) => factory();
    public void Dispose() => IsDisposed = true;
}
internal sealed class FixedEmailAudienceResolver : Notifications.Application.Contracts.Delivery.INotificationAudienceResolver
{
    public Task<IReadOnlyList<Shared.EventContracts.NotificationRecipientEto>> ResolveAsync(
        Guid? tenantId,
        Shared.EventContracts.NotificationSendChannel channel,
        IReadOnlyList<Shared.EventContracts.NotificationRecipientEto> recipients,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Shared.EventContracts.NotificationRecipientEto>>(
        [
            new Shared.EventContracts.NotificationRecipientEto
            {
                RecipientType = Shared.EventContracts.NotificationSendRecipientType.EmailAddress,
                RecipientValue = "group-user@test.local"
            }
        ]);
}

internal sealed class DisposeAction : IDisposable
{
    private readonly Action _action;
    public DisposeAction(Action action) => _action = action;
    public void Dispose() => _action();
}

/// <summary>
/// Minimal renderer stub for tests that construct the delivery worker but do not exercise the
/// App-channel inbox render path. Returns fixed content and records the last call.
/// </summary>
internal sealed class StubNotificationsTemplateRenderer
    : Notifications.Application.Contracts.Templates.INotificationsTemplateRenderer
{
    public string Content { get; set; } = "<p>rendered body</p>";
    public string? Subject { get; set; } = "rendered subject";
    public bool? LastApplyLayout { get; private set; }

    public Task<Notifications.Application.Contracts.Templates.RenderedNotificationTemplate> RenderAsync(
        string templateName,
        Dictionary<string, string?> model,
        CancellationToken cancellationToken = default)
        => RenderAsync(templateName, model, applyLayout: true, cancellationToken);

    public Task<Notifications.Application.Contracts.Templates.RenderedNotificationTemplate> RenderAsync(
        string templateName,
        Dictionary<string, string?> model,
        bool applyLayout,
        CancellationToken cancellationToken = default)
    {
        LastApplyLayout = applyLayout;
        return Task.FromResult(new Notifications.Application.Contracts.Templates.RenderedNotificationTemplate
        {
            Content = Content,
            Subject = Subject
        });
    }

    public Task<Notifications.Application.Contracts.Templates.RenderedNotificationTemplate> RenderDraftAsync(
        string templateName,
        string bodyTemplate,
        string? subjectTemplate,
        Dictionary<string, string?> model,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new Notifications.Application.Contracts.Templates.RenderedNotificationTemplate
        {
            Content = Content,
            Subject = Subject
        });
}

internal sealed class PassThroughRateLimiter : Notifications.Application.Contracts.RateLimiting.INotificationRateLimiter
{
    public Task EnsureWithinLimitAsync(
        Guid operationId,
        Guid? tenantId,
        Notifications.Domain.Shared.Enums.NotificationChannel channel,
        int messageCount,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal sealed class PassThroughAudienceResolver : Notifications.Application.Contracts.Delivery.INotificationAudienceResolver
{
    public Task<IReadOnlyList<Shared.EventContracts.NotificationRecipientEto>> ResolveAsync(
        Guid? tenantId,
        Shared.EventContracts.NotificationSendChannel channel,
        IReadOnlyList<Shared.EventContracts.NotificationRecipientEto> recipients,
        CancellationToken cancellationToken = default)
        => Task.FromResult(recipients);
}

internal sealed class TestCurrentTenant : ICurrentTenant
{
    public bool IsAvailable => Id.HasValue;
    public Guid? Id { get; private set; }
    public string? Name { get; private set; }

    public TestCurrentTenant(Guid? id)
    {
        Id = id;
    }

    public IDisposable Change(Guid? id, string? name = null)
    {
        var previousId = Id;
        var previousName = Name;
        Id = id;
        Name = name;
        return new DisposeAction(() =>
        {
            Id = previousId;
            Name = previousName;
        });
    }
}

internal sealed class TestCurrentPrincipalAccessor : ICurrentPrincipalAccessor
{
    private ClaimsPrincipal _principal;

    public ClaimsPrincipal Principal => _principal;

    public TestCurrentPrincipalAccessor(string userId)
    {
        _principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim(SharedClaimTypes.Subject, userId)
                ],
                authenticationType: "Test"));
    }

    public IDisposable Change(ClaimsPrincipal principal)
    {
        var previous = _principal;
        _principal = principal;
        return new DisposeAction(() => _principal = previous);
    }
}

/// <summary>
/// Data filter whose enabled state is fixed at construction. When enabled, the shared multi-tenant
/// (and soft-delete) query filters are active, matching production; when disabled, they are off.
/// </summary>
internal sealed class ConfigurableDataFilter : IDataFilter
{
    private readonly bool _enabled;

    public int DisableCount { get; private set; }

    public ConfigurableDataFilter(bool enabled)
    {
        _enabled = enabled;
    }

    public IDisposable Enable<TFilter>() where TFilter : class => new DisposeAction(() => { });

    public IDisposable Disable<TFilter>() where TFilter : class
    {
        DisableCount++;
        return new DisposeAction(() => { });
    }

    public bool IsEnabled<TFilter>() where TFilter : class => _enabled;
}

internal sealed class EfTestRepository<TEntity, TKey> : Shared.EntityFrameworkCore.Repositories.IEfCoreRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    private readonly NotificationsDbContextApp _dbContext;

    public EfTestRepository(NotificationsDbContextApp dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<DbContext> GetDbContextAsync() => Task.FromResult<DbContext>(_dbContext);
    public Task<DbSet<TEntity>> GetDbSetAsync() => Task.FromResult(_dbContext.Set<TEntity>());

    public string? EntityName { get; set; }
    public string ProviderName => "EfCoreInMemory";

    public Task<IQueryable<TEntity>> WithDetailsAsync()
    {
        return Task.FromResult(_dbContext.Set<TEntity>().AsQueryable());
    }

    public Task<IQueryable<TEntity>> WithDetailsAsync(params Expression<Func<TEntity, object>>[] propertySelectors)
    {
        return Task.FromResult(_dbContext.Set<TEntity>().AsQueryable());
    }

    public Task<IQueryable<TEntity>> GetQueryableAsync()
    {
        return Task.FromResult(_dbContext.Set<TEntity>().AsQueryable());
    }

    public Task<TEntity?> FindAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default)
    {
        return _dbContext.Set<TEntity>().FindAsync([id], cancellationToken).AsTask();
    }

    public async Task<TEntity> GetAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, includeDetails, cancellationToken);
        return entity ?? throw new InvalidOperationException("Entity not found.");
    }

    public Task<TEntity?> FindAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Set<TEntity>().FirstOrDefaultAsync(predicate, cancellationToken);
    }

    public async Task<TEntity> GetAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(predicate, includeDetails, cancellationToken);
        return entity ?? throw new InvalidOperationException("Entity not found.");
    }

    public async Task DeleteAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.Set<TEntity>().Where(predicate).ToListAsync(cancellationToken);
        _dbContext.Set<TEntity>().RemoveRange(entities);
        if (autoSave)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<TEntity> InsertAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        await _dbContext.Set<TEntity>().AddAsync(entity, cancellationToken);
        if (autoSave)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        return entity;
    }

    public async Task InsertManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        await _dbContext.Set<TEntity>().AddRangeAsync(entities, cancellationToken);
        if (autoSave)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public Task<TEntity> UpdateAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        _dbContext.Set<TEntity>().Update(entity);
        return autoSave
            ? SaveAndReturnAsync(entity, cancellationToken)
            : Task.FromResult(entity);
    }

    public Task UpdateManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        _dbContext.Set<TEntity>().UpdateRange(entities);
        return autoSave ? _dbContext.SaveChangesAsync(cancellationToken) : Task.CompletedTask;
    }

    public Task DeleteAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        _dbContext.Set<TEntity>().Remove(entity);
        return autoSave ? _dbContext.SaveChangesAsync(cancellationToken) : Task.CompletedTask;
    }

    public Task DeleteManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        _dbContext.Set<TEntity>().RemoveRange(entities);
        return autoSave ? _dbContext.SaveChangesAsync(cancellationToken) : Task.CompletedTask;
    }

    public Task DeleteAsync(TKey id, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        return DeleteAsync(x => EqualityComparer<TKey>.Default.Equals(x.Id, id), autoSave, cancellationToken);
    }

    public Task DeleteManyAsync(IEnumerable<TKey> ids, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var set = ids.ToHashSet();
        return DeleteAsync(x => set.Contains(x.Id), autoSave, cancellationToken);
    }

    public Task DeleteDirectAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Set<TEntity>().Where(predicate).ExecuteDeleteAsync(cancellationToken);
    }

    public Task<List<TEntity>> GetListAsync(bool includeDetails = false, CancellationToken cancellationToken = default)
    {
        return _dbContext.Set<TEntity>().ToListAsync(cancellationToken);
    }

    public Task<List<TEntity>> GetListAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = false,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Set<TEntity>().Where(predicate).ToListAsync(cancellationToken);
    }

    public Task<long> GetCountAsync(CancellationToken cancellationToken = default)
    {
        return _dbContext.Set<TEntity>().LongCountAsync(cancellationToken);
    }

    public Task<List<TEntity>> GetPagedListAsync(
        int skipCount,
        int maxResultCount,
        string sorting,
        bool includeDetails = false,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Set<TEntity>()
            .Skip(skipCount)
            .Take(maxResultCount)
            .ToListAsync(cancellationToken);
    }

    private async Task<TEntity> SaveAndReturnAsync(TEntity entity, CancellationToken cancellationToken)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }
}

internal sealed class NoopAuditPropertySetter : IAuditPropertySetter
{
    public void SetCreationProperties(object entity) { }
    public void SetModificationProperties(object entity) { }
    public void SetDeletionProperties(object entity) { }
    public void IncrementEntityVersionProperty(object entity) { }
}

internal sealed class NoopAuditingManager : IAuditingManager
{
    public IAuditLogScope? Current { get; } = new NoopAuditLogScope();

    public IAuditLogSaveHandle BeginScope()
    {
        return NoopAuditLogSaveHandle.Instance;
    }
}

internal sealed class NoopAuditLogScope : IAuditLogScope
{
    public AuditLogInfo Log { get; } = new();
}

internal sealed class NoopAuditLogSaveHandle : IAuditLogSaveHandle
{
    public static readonly NoopAuditLogSaveHandle Instance = new();

    public Task SaveAsync() => Task.CompletedTask;

    public void Dispose()
    {
    }
}

/// <summary>
/// Unit-of-work manager whose ambient <see cref="Current"/> is always null (so the shared
/// DbContext skips outbox/event bookkeeping) while <see cref="Begin"/> returns a no-op unit of
/// work for callers such as the delivery scheduler that open and complete their own scope.
/// </summary>
internal sealed class NoopUnitOfWorkManager : IUnitOfWorkManager
{
    public IUnitOfWork? Current => null;
    public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => new NoopUnitOfWork(options);
    public IUnitOfWork Begin(bool requiresNew = false) => new NoopUnitOfWork(new UnitOfWorkOptions());
    public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => new NoopUnitOfWork(new UnitOfWorkOptions());
    public void BeginReserved(string reservationName, UnitOfWorkOptions options) { }
    public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => true;
}

internal sealed class NoopUnitOfWork : IUnitOfWork
{
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
