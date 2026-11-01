using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.AuditLogs.Auditing;
using Shared.Data.ConnectionStrings;
using Shared.Data.Filtering;
using Shared.Data.Seeding;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.EntityFrameworkCore.ConnectionStrings;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;
using Shared.MultiTenancy;
using Shared.Threading;
using Shared.Uow.Abstractions;
using Shared.Uow.Context;
using Shared.Uow.Implementations;
using Shared.Uow.Options;

namespace Shared.EntityFrameworkCore.Tests.TestDoubles;

/// <summary>
/// The production tenant-database stack over real PostgreSQL: the tenant connection-string resolver with shared
/// fallback, the cached tenant store contract, the unit-of-work DbContext provider, topology, fan-out, migration guard
/// and migrator. Tests add the service DbContext and the application services they exercise.
/// </summary>
internal sealed class TenantDatabaseTestHost : IAsyncDisposable
{
    private TenantDatabaseTestHost(ServiceProvider services, InMemoryTenantStore tenants, InMemoryDistributedLock locks)
    {
        Services = services;
        Tenants = tenants;
        Locks = locks;
    }

    public ServiceProvider Services { get; }

    public InMemoryTenantStore Tenants { get; }

    public InMemoryDistributedLock Locks { get; }

    public TestAmbientTenant CurrentTenant => (TestAmbientTenant)Services.GetRequiredService<ICurrentTenant>();

    public static TenantDatabaseTestHost Create(string sharedConnectionString, Action<IServiceCollection> configureServices)
    {
        var tenants = new InMemoryTenantStore();
        var locks = new InMemoryDistributedLock();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddOptions();

        services.Configure<UnitOfWorkDefaultOptions>(_ => { });
        services.Configure<SharedDbContextOptions>(_ => { });
        services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        services.AddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();
        services.AddTransient<IUnitOfWorkExecutor, BeginningUnitOfWorkExecutor>();
        services.AddSingleton<ICancellationTokenProvider>(new NoneCancellationTokenProvider());
        services.AddSingleton<ICurrentTenant, TestAmbientTenant>();
        services.AddSingleton<IDataFilter, DataFilter>();
        services.AddSingleton(typeof(IDataFilter<>), typeof(DataFilter<>));
        services.Configure<SharedDataFilterOptions>(_ => { });
        services.AddSingleton<IAuditPropertySetter, NoAuditPropertySetter>();
        services.AddSingleton<IAuditingManager, NoAuditingManager>();
        services.AddSingleton<IEfCoreDbContextTypeProvider, IdentityDbContextTypeProvider>();
        services.AddTransient(typeof(IDbContextProvider<>), typeof(UnitOfWorkDbContextProvider<>));

        services.Configure<SharedDbConnectionOptions>(options => options.ConnectionStrings.Default = sharedConnectionString);
        services.Configure<SharedConnectionStringResolverOptions>(options =>
        {
            options.AllowHostFallbackForTenant = true;
            options.TenantCacheSeconds = 1;
        });
        services.AddTransient<IConnectionStringResolver, TenantConnectionStringResolver>();
        services.AddSingleton<ITenantStore>(tenants);
        services.AddSingleton<ITenantConnectionStringProtector, PlaintextTenantConnectionStringProtector>();
        services.AddSingleton<ITenantConnectionStringsCache, TenantConnectionStringsCache>();
        services.AddSingleton<IDistributedLock>(locks);
        services.AddSingleton<ITenantDatabaseTopology, TenantDatabaseTopology>();
        services.AddSingleton<TenantDatabaseFanOut>();
        services.AddSingleton<TenantDatabaseMigrationGuard>();
        services.AddTransient<TenantDatabaseMigrator>();

        configureServices(services);
        return new TenantDatabaseTestHost(
            services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }),
            tenants,
            locks);
    }

    /// <summary>Runs <paramref name="work"/> in a unit of work under <paramref name="tenantId"/> and completes it.</summary>
    public async Task<T> InUnitOfWorkAsync<T>(Guid? tenantId, Func<IServiceProvider, Task<T>> work, bool transactional = true)
    {
        using var tenant = CurrentTenant.Change(tenantId);
        await using var scope = Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        using var unitOfWork = manager.Begin(new UnitOfWorkOptions { IsTransactional = transactional }, requiresNew: true);
        var result = await work(scope.ServiceProvider);
        await unitOfWork.CompleteAsync();
        return result;
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    /// <summary>Begins the unit of work the production executor's interceptor would begin.</summary>
    private sealed class BeginningUnitOfWorkExecutor(IUnitOfWorkManager manager) : IUnitOfWorkExecutor
    {
        public Task ExecuteTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
            RunAsync(true, async token => { await operation(token); return true; }, cancellationToken);

        public Task<TResult> ExecuteTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default) =>
            RunAsync(true, operation, cancellationToken);

        public Task ExecuteNonTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
            RunAsync(false, async token => { await operation(token); return true; }, cancellationToken);

        public Task<TResult> ExecuteNonTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default) =>
            RunAsync(false, operation, cancellationToken);

        private async Task<TResult> RunAsync<TResult>(
            bool transactional,
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            using var unitOfWork = manager.Begin(new UnitOfWorkOptions { IsTransactional = transactional }, requiresNew: true);
            var result = await operation(cancellationToken);
            await unitOfWork.CompleteAsync(cancellationToken);
            return result;
        }
    }

    private sealed class NoneCancellationTokenProvider : ICancellationTokenProvider
    {
        public CancellationToken Token => CancellationToken.None;
    }

    private sealed class IdentityDbContextTypeProvider : IEfCoreDbContextTypeProvider
    {
        public Type GetDbContextType(Type dbContextType) => dbContextType;
    }

    private sealed class NoAuditPropertySetter : IAuditPropertySetter
    {
        public void SetCreationProperties(object entity) { }
        public void SetModificationProperties(object entity) { }
        public void SetDeletionProperties(object entity) { }
        public void IncrementEntityVersionProperty(object entity) { }
    }

    private sealed class NoAuditingManager : IAuditingManager
    {
        public IAuditLogScope? Current => null;
        public IAuditLogSaveHandle BeginScope() => throw new NotSupportedException("Auditing is outside this suite.");
    }

    private sealed class PlaintextTenantConnectionStringProtector : ITenantConnectionStringProtector
    {
        public string Protect(Guid tenantId, string connectionName, string plaintext) => plaintext;
        public string Unprotect(Guid tenantId, string connectionName, string storedValue) => storedValue;
        public bool IsProtected(string? storedValue) => false;
    }
}

/// <summary>The cached tenant store as services see it: per-tenant entries plus the list with dedicated names.</summary>
internal sealed class InMemoryTenantStore : ITenantStore
{
    private readonly ConcurrentDictionary<Guid, TenantConfiguration> _tenants = new();

    /// <summary>Simulates a cold or stale tenant list in the cache.</summary>
    public bool ListAvailable { get; set; } = true;

    public void Upsert(Guid id, bool isActive = true, params (string Name, string ConnectionString)[] dedicated)
    {
        var tenant = new TenantConfiguration
        {
            Id = id,
            Name = $"tenant-{id:N}",
            NormalizedName = $"TENANT-{id:N}",
            IsActive = isActive,
            DedicatedDatabaseNames = dedicated.Select(database => database.Name).Order(StringComparer.Ordinal).ToArray()
        };
        foreach (var (name, connectionString) in dedicated)
        {
            tenant.ConnectionStrings[name] = connectionString;
        }

        _tenants[id] = tenant;
    }

    [Obsolete("Use FindAsync(Guid, CancellationToken) for non-blocking tenant resolution.")]
    public TenantConfiguration? Find(Guid id) => _tenants.GetValueOrDefault(id);

    [Obsolete("Use FindAsync(string, CancellationToken) for non-blocking tenant resolution.")]
    public TenantConfiguration? Find(string normalizedName) => null;

    [Obsolete("Use GetListAsync(CancellationToken) for non-blocking tenant resolution.")]
    public IReadOnlyList<TenantConfiguration> GetList() => [.. _tenants.Values];

    public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tenants.GetValueOrDefault(id));

    public Task<TenantConfiguration?> FindAsync(string normalizedName, CancellationToken cancellationToken = default) =>
        Task.FromResult<TenantConfiguration?>(null);

    public Task<IReadOnlyList<TenantConfiguration>?> FindListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TenantConfiguration>?>(ListAvailable ? [.. _tenants.Values] : null);

    public Task<IReadOnlyList<TenantConfiguration>> GetListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TenantConfiguration>>(ListAvailable ? [.. _tenants.Values] : []);
}

/// <summary>A process-local distributed lock whose names the test can hold to prove serialization.</summary>
internal sealed class InMemoryDistributedLock : IDistributedLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _acquired = new();

    public IReadOnlyCollection<string> AcquiredNames => [.. _acquired];

    public async Task<IAsyncDisposable> HoldAsync(string name)
    {
        var semaphore = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        return new Handle(name, semaphore);
    }

    public async Task<IDistributedLockHandle?> TryAcquireAsync(
        string name,
        DistributedLockAcquireOptions options,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(options.AcquireTimeoutMs ?? 0, cancellationToken))
        {
            return null;
        }

        _acquired.Enqueue(name);
        return new Handle(name, semaphore);
    }

    public async Task<IDistributedLockHandle> AcquireAsync(
        string name,
        DistributedLockAcquireOptions options,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        _acquired.Enqueue(name);
        return new Handle(name, semaphore);
    }

    private sealed class Handle(string name, SemaphoreSlim semaphore) : IDistributedLockHandle
    {
        private int _released;

        public string Name => name;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Records every seeding pass the migrator runs, with its tenant.</summary>
internal sealed class RecordingDataSeeder : IDataSeeder
{
    public ConcurrentQueue<Guid?> SeededTenants { get; } = new();

    public Task SeedAsync(DataSeedContext context)
    {
        SeededTenants.Enqueue(context.TenantId);
        return Task.CompletedTask;
    }
}
