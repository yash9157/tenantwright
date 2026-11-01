using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Data;
using Shared.Data.ConnectionStrings;
using Shared.Data.Seeding;
using Shared.EntityFrameworkCore.ConnectionStrings;
using Shared.Modularity.DependencyInjection;
using Shared.Modularity.Extensions;
using Shared.Modularity.Modules;
using Shared.MultiTenancy;
using Shared.Uow.Attributes;
using Shared.Uow.Abstractions;
using Shared.Uow.Events;
using Shared.Uow.Options;
using Xunit;

namespace Shared.Data.Tests;

public class SharedDataHardeningTests
{
    [Fact]
    public void SharedData_module_should_boot_without_event_bus_dependency()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var application = services.AddApplication<DataOnlyTestModule>(configuration);

        Assert.NotNull(application);
    }

    [Fact]
    public void SharedData_should_auto_discover_seed_contributors_and_keep_unique_deterministic_order()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        services.AddApplication<AutoSeedTestModule>(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SharedDataSeedOptions>>().Value;
        var contributors = options.Contributors.ToList();

        Assert.Contains(typeof(AlphaDataSeedContributor), contributors);
        Assert.Contains(typeof(BetaDataSeedContributor), contributors);
        Assert.Equal(2, contributors.Count);
        Assert.Equal(
            contributors.OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray(),
            contributors.ToArray());
    }

    [Fact]
    public void DataSeeder_seed_method_should_be_unit_of_work_enabled()
    {
        var method = typeof(DataSeeder).GetMethod(nameof(DataSeeder.SeedAsync), BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<UnitOfWorkAttribute>());
    }

    [Fact]
    public async Task DataSeeder_should_resolve_contributors_from_the_active_unit_of_work_scope()
    {
        var contributor = new ScopeTrackingSeedContributor();
        await using var unitOfWorkProvider = new ServiceCollection()
            .AddSingleton(contributor)
            .AddSingleton<IDataSeedContributor>(contributor)
            .BuildServiceProvider();
        var unitOfWork = new TestUnitOfWork(unitOfWorkProvider);
        var manager = new TestUnitOfWorkManager(unitOfWork);
        var seedOptions = new SharedDataSeedOptions();
        seedOptions.Contributors.Add<ScopeTrackingSeedContributor>();
        var seeder = new DataSeeder(Options.Create(seedOptions), manager);

        await seeder.SeedAsync(new DataSeedContext());

        Assert.True(contributor.Executed);
    }

    [Fact]
    public async Task Default_connection_resolver_should_fail_fast_when_missing_and_strict_mode_enabled()
    {
        var resolver = new DefaultConnectionStringResolver(
            options: new TestOptionsMonitor<SharedDbConnectionOptions>(new SharedDbConnectionOptions()),
            resolverOptions: new TestOptionsMonitor<SharedConnectionStringResolverOptions>(new SharedConnectionStringResolverOptions { ThrowOnMissing = true }),
            logger: NullLogger<DefaultConnectionStringResolver>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync("Missing"));
    }

    [Fact]
    public async Task Tenant_connection_resolver_should_not_return_empty_string_when_missing_and_strict_mode_enabled()
    {
        var resolver = new TenantConnectionStringResolver(
            currentTenant: new TestCurrentTenant(null),
            tenantStore: new TestTenantStore(),
            tenantConnectionStringsCache: new TestTenantConnectionStringsCache(),
            tenantConnectionStringProtector: new NoopTenantConnectionStringProtector(),
            options: new TestOptionsMonitor<SharedDbConnectionOptions>(new SharedDbConnectionOptions()),
            resolverOptions: new TestOptionsMonitor<SharedConnectionStringResolverOptions>(new SharedConnectionStringResolverOptions { ThrowOnMissing = true }),
            logger: NullLogger<TenantConnectionStringResolver>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(global::Shared.Data.ConnectionStrings.ConnectionStrings.DefaultConnectionStringName));
    }

    [DependsOn(typeof(SharedDataModule))]
    private sealed class DataOnlyTestModule : BaseModule
    {
    }

    [DependsOn(typeof(SharedDataModule))]
    private sealed class AutoSeedTestModule : BaseModule
    {
        public override void ConfigureServices(Shared.Modularity.Context.ServiceConfigurationContext context)
        {
            context.Services.Configure<SharedDataSeedOptions>(options =>
            {
                options.Contributors.Add<AlphaDataSeedContributor>();
                options.Contributors.Add<AlphaDataSeedContributor>();
            });
        }
    }

    public sealed class AlphaDataSeedContributor : IDataSeedContributor, ITransientDependency
    {
        public Task SeedAsync(DataSeedContext context)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class BetaDataSeedContributor : IDataSeedContributor, ITransientDependency
    {
        public Task SeedAsync(DataSeedContext context)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ScopeTrackingSeedContributor : IDataSeedContributor
    {
        public bool Executed { get; private set; }

        public Task SeedAsync(DataSeedContext context)
        {
            Executed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class TestUnitOfWorkManager(IUnitOfWork current) : IUnitOfWorkManager
    {
        public IUnitOfWork? Current { get; } = current;
        public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => throw new NotSupportedException();
        public IUnitOfWork Begin(bool requiresNew = false) => throw new NotSupportedException();
        public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => throw new NotSupportedException();
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) => throw new NotSupportedException();
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => false;
    }

    private sealed class TestUnitOfWork(IServiceProvider serviceProvider) : IUnitOfWork
    {
        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
        public event EventHandler<UnitOfWorkFailedEventArgs>? Failed
        {
            add { }
            remove { }
        }
        public event EventHandler<UnitOfWorkEventArgs>? Disposed;
        public UnitOfWorkOptions Options { get; } = new();
        public IUnitOfWork? Outer { get; private set; }
        public bool IsReserved { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool IsCompleted { get; private set; }
        public string? ReservationName { get; private set; }
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public void SetOuter(IUnitOfWork? outer) => Outer = outer;
        public void Initialize(UnitOfWorkOptions options) { }
        public void Reserve(string reservationName)
        {
            IsReserved = true;
            ReservationName = reservationName;
        }
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

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; }
        public string? Name => null;

        public IDisposable Change(Guid? id, string? name = null)
        {
            return new NoopDisposable();
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class TestTenantStore : ITenantStore
    {
        public TenantConfiguration? Find(Guid id)
        {
            return null;
        }

        public TenantConfiguration? Find(string normalizedName)
        {
            return null;
        }

        public IReadOnlyList<TenantConfiguration> GetList()
        {
            return Array.Empty<TenantConfiguration>();
        }

        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TenantConfiguration?>(null);
        }

        public Task<TenantConfiguration?> FindAsync(string normalizedName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TenantConfiguration?>(null);
        }

        public async Task<IReadOnlyList<TenantConfiguration>?> FindListAsync(CancellationToken cancellationToken = default) =>
            await GetListAsync(cancellationToken);

        public Task<IReadOnlyList<TenantConfiguration>> GetListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TenantConfiguration>>(Array.Empty<TenantConfiguration>());
        }
    }

    private sealed class TestTenantConnectionStringsCache : ITenantConnectionStringsCache
    {
        public Task<TenantConnectionStringsCacheItem> GetOrAddAsync(
            Guid tenantId,
            Func<Task<TenantConnectionStringsCacheItem>> factory,
            CancellationToken cancellationToken = default)
        {
            return factory();
        }

        public Task RemoveAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoopTenantConnectionStringProtector : ITenantConnectionStringProtector
    {
        public string Protect(Guid tenantId, string connectionName, string plaintext) => plaintext;

        public string Unprotect(Guid tenantId, string connectionName, string storedValue) => storedValue;

        public bool IsProtected(string? storedValue) => false;
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class, new()
    {
        public TestOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; }

        public T Get(string? name)
        {
            return CurrentValue;
        }

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            return null;
        }
    }
}
