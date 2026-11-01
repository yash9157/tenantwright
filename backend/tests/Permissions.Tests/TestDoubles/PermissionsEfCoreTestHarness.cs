using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.AuditLogs.Auditing;
using Shared.Data.Filtering;
using Shared.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.MultiTenancy;
using Shared.Uow;
using Shared.Uow.Abstractions;
using Shared.Uow.Events;
using Shared.Uow.Options;

namespace Permissions.Tests.TestDoubles;

/// <summary>
/// Minimal harness for building a real <see cref="PermissionsDbContextApp"/> over SQLite (relational),
/// needed because the grant sweep uses <c>ExecuteDelete</c> and <c>IgnoreQueryFilters</c>, neither of
/// which the EF Core InMemory provider supports/enforces the way production PostgreSQL does. Mirrors
/// the Notifications test harness pattern.
/// </summary>
internal static class PermissionsEfCoreTestHarness
{
    public static PermissionsDbContextApp BuildSqliteDbContext(
        SqliteConnection connection,
        Guid? tenantId,
        bool multiTenantFilterEnabled = false,
        IInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PermissionsDbContextApp>()
            .UseSqlite(connection);
        if (interceptor != null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        var dbContext = new PermissionsDbContextApp(optionsBuilder.Options)
        {
            ServiceProvider = CreateServiceProvider(tenantId, multiTenantFilterEnabled)
        };
        dbContext.Initialize(new EfCoreDbContextInitializationContext(new NoopUnitOfWork(new UnitOfWorkOptions())));
        return dbContext;
    }

    public static IDbContextProvider<PermissionsDbContextApp> AsDbContextProvider(PermissionsDbContextApp dbContext)
        => new TestDbContextProvider(dbContext);

    private sealed class TestDbContextProvider(PermissionsDbContextApp dbContext)
        : IDbContextProvider<PermissionsDbContextApp>
    {
        public Task<PermissionsDbContextApp> GetDbContextAsync()
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
        services.AddSingleton<ITenantWrightMessageSender>(new NoopTenantWrightMessageSender());

        return services.BuildServiceProvider();
    }
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

internal sealed class DisposeAction : IDisposable
{
    private readonly Action _action;
    public DisposeAction(Action action) => _action = action;
    public void Dispose() => _action();
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

internal sealed class ConfigurableDataFilter : IDataFilter
{
    private readonly bool _defaultEnabled;
    private readonly Dictionary<Type, bool> _states = [];

    public ConfigurableDataFilter(bool enabled)
    {
        _defaultEnabled = enabled;
    }

    public IDisposable Enable<TFilter>() where TFilter : class => Change<TFilter>(true);

    public IDisposable Disable<TFilter>() where TFilter : class => Change<TFilter>(false);

    public bool IsEnabled<TFilter>() where TFilter : class
        => _states.GetValueOrDefault(typeof(TFilter), _defaultEnabled);

    private DisposeAction Change<TFilter>(bool enabled) where TFilter : class
    {
        var type = typeof(TFilter);
        var hadPreviousState = _states.TryGetValue(type, out var previousState);
        _states[type] = enabled;
        return new DisposeAction(() =>
        {
            if (hadPreviousState)
            {
                _states[type] = previousState;
                return;
            }

            _states.Remove(type);
        });
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

internal sealed class NoopTenantWrightMessageSender : ITenantWrightMessageSender
{
    public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class =>
        Task.CompletedTask;

    public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class =>
        Task.CompletedTask;
}


internal static class PermissionsCachingTestHarness
{
    /// <summary>
    /// Builds a minimal but real hybrid-cache DI stack (in-memory backing store, no Valkey) so tests can
    /// construct concrete cache services like <c>PermissionStampService</c> and
    /// <c>PermissionProviderGrantCacheSyncService</c> without a Valkey dependency.
    /// </summary>
    public static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddDistributedMemoryCache();
        services.AddHybridCache();

        services.AddSingleton<Shared.Caching.Serialization.CacheSerializer>();
        services.AddSingleton<Shared.Caching.Abstractions.IDistributedCacheSerializer, Shared.Caching.Implementations.DefaultDistributedCacheSerializer>();
        services.AddSingleton<Shared.Caching.Abstractions.IDistributedCacheKeyNormalizer, Shared.Caching.Implementations.DefaultDistributedCacheKeyNormalizer>();
        services.AddSingleton<Shared.Threading.ICancellationTokenProvider, Shared.Caching.Implementations.DefaultCancellationTokenProvider>();
        services.AddSingleton<Shared.Caching.Abstractions.ICacheTenantResolver, Shared.Caching.Abstractions.NullCacheTenantResolver>();
        services.Configure<Shared.Caching.SharedDistributedCacheOptions>(_ => { });
        services.Configure<Shared.Caching.SharedHybridCacheOptions>(_ => { });
        services.AddSingleton<IUnitOfWorkManager>(new NoopUnitOfWorkManager());
        services.AddSingleton<ITenantWrightMessageSender>(new NoopTenantWrightMessageSender());

        services.AddSingleton<Permissions.Application.Caching.PermissionProviderStampCache>();
        services.AddSingleton<Permissions.Application.Caching.PermissionProviderGrantsCache>();
        services.AddSingleton<Permissions.Application.Caching.PermissionSnapshotCache>();
        services.AddSingleton<Permissions.Application.Caching.RolePrivilegeMatrixCache>();

        return services.BuildServiceProvider();
    }
}

internal sealed class EfTestRepository<TEntity, TKey> : Shared.Domain.Repositories.IRepository<TEntity, TKey>
    where TEntity : class, Shared.Domain.Entities.IEntity<TKey>
{
    private readonly PermissionsDbContextApp _dbContext;

    public EfTestRepository(PermissionsDbContextApp dbContext)
    {
        _dbContext = dbContext;
    }

    public string? EntityName { get; set; }
    public string ProviderName => "EfCoreSqlite";

    public Task<IQueryable<TEntity>> WithDetailsAsync()
        => Task.FromResult(_dbContext.Set<TEntity>().AsQueryable());

    public Task<IQueryable<TEntity>> WithDetailsAsync(params System.Linq.Expressions.Expression<Func<TEntity, object>>[] propertySelectors)
        => Task.FromResult(_dbContext.Set<TEntity>().AsQueryable());

    public Task<IQueryable<TEntity>> GetQueryableAsync()
        => Task.FromResult(_dbContext.Set<TEntity>().AsQueryable());

    public Task<TEntity?> FindAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().FindAsync([id], cancellationToken).AsTask();

    public async Task<TEntity> GetAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, includeDetails, cancellationToken);
        return entity ?? throw new InvalidOperationException("Entity not found.");
    }

    public Task<TEntity?> FindAsync(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().FirstOrDefaultAsync(predicate, cancellationToken);

    public async Task<TEntity> GetAsync(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(predicate, includeDetails, cancellationToken);
        return entity ?? throw new InvalidOperationException("Entity not found.");
    }

    public async Task DeleteAsync(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
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
        return autoSave ? SaveAndReturnAsync(entity, cancellationToken) : Task.FromResult(entity);
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
        => DeleteAsync(x => EqualityComparer<TKey>.Default.Equals(x.Id, id), autoSave, cancellationToken);

    public Task DeleteManyAsync(IEnumerable<TKey> ids, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var set = ids.ToHashSet();
        return DeleteAsync(x => set.Contains(x.Id), autoSave, cancellationToken);
    }

    public Task DeleteDirectAsync(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().Where(predicate).ExecuteDeleteAsync(cancellationToken);

    public Task<List<TEntity>> GetListAsync(bool includeDetails = false, CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().ToListAsync(cancellationToken);

    public Task<List<TEntity>> GetListAsync(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = false,
        CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().Where(predicate).ToListAsync(cancellationToken);

    public Task<long> GetCountAsync(CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().LongCountAsync(cancellationToken);

    public Task<List<TEntity>> GetPagedListAsync(
        int skipCount,
        int maxResultCount,
        string sorting,
        bool includeDetails = false,
        CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().Skip(skipCount).Take(maxResultCount).ToListAsync(cancellationToken);

    private async Task<TEntity> SaveAndReturnAsync(TEntity entity, CancellationToken cancellationToken)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
