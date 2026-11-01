using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore.ConnectionStrings;
using Shared.MultiTenancy;
using Xunit;

namespace Shared.MultiTenancy.Tests;

/// <summary>
/// Resolution order: host scope uses the host configuration; tenant scope fails closed for unknown and inactive
/// tenants, uses the tenant's own connection string only for its exact dedicatable name, then the host database when
/// fallback is allowed, then fails closed. Host-only catalog names never consult tenant values.
/// </summary>
public class TenantConnectionStringResolverTests
{
    private const string HostDefault = "Host=shared-db;Database=tenantwright;Username=app;Password=host-secret";
    private const string Dedicated = "Host=tenant-db;Database=acme_notifications;Username=acme;Password=tenant-secret";
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Host_scope_resolves_every_catalog_name_through_the_default_connection_string()
    {
        var resolver = CreateResolver(currentTenantId: null, tenant: null);

        foreach (var name in TenantWrightDatabaseCatalog.All)
        {
            Assert.Equal(HostDefault, await resolver.ResolveAsync(name));
        }

        Assert.Equal(HostDefault, await resolver.ResolveAsync());
    }

    [Fact]
    public async Task Host_scope_prefers_an_explicit_named_connection_string()
    {
        var options = HostOptions();
        options.ConnectionStrings[TenantWrightDatabaseCatalog.Notifications] = "Host=notifications-db;Database=n";
        var resolver = CreateResolver(currentTenantId: null, tenant: null, hostOptions: options);

        Assert.Equal("Host=notifications-db;Database=n", await resolver.ResolveAsync(TenantWrightDatabaseCatalog.Notifications));
        Assert.Equal(HostDefault, await resolver.ResolveAsync(TenantWrightDatabaseCatalog.AuditLogs));
    }

    [Fact]
    public async Task Tenant_scope_returns_the_dedicated_database_for_its_exact_name()
    {
        var resolver = CreateResolver(TenantId, Tenant(connectionStrings: (TenantWrightDatabaseCatalog.Notifications, Dedicated)));

        Assert.Equal(Dedicated, await resolver.ResolveAsync(TenantWrightDatabaseCatalog.Notifications));
    }

    [Fact]
    public async Task Tenant_scope_falls_back_to_the_shared_database_for_other_names_when_allowed()
    {
        var resolver = CreateResolver(
            TenantId,
            Tenant(connectionStrings: (TenantWrightDatabaseCatalog.Notifications, Dedicated)),
            allowHostFallback: true);

        Assert.Equal(HostDefault, await resolver.ResolveAsync(TenantWrightDatabaseCatalog.Permissions));
        Assert.Equal(HostDefault, await resolver.ResolveAsync(TenantWrightDatabaseCatalog.AuditLogs));
    }

    [Fact]
    public async Task Tenant_scope_fails_closed_without_a_dedicated_database_when_fallback_is_disabled()
    {
        var resolver = CreateResolver(TenantId, Tenant(), allowHostFallback: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(TenantWrightDatabaseCatalog.Notifications));
    }

    [Fact]
    public async Task Tenant_default_connection_string_is_never_a_fallback_for_service_names()
    {
        var resolver = CreateResolver(
            TenantId,
            Tenant(connectionStrings: (ConnectionStrings.DefaultConnectionStringName, Dedicated)),
            allowHostFallback: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(TenantWrightDatabaseCatalog.Notifications));
    }

    [Theory]
    [InlineData(TenantWrightDatabaseCatalog.Auth)]
    [InlineData(TenantWrightDatabaseCatalog.Identity)]
    [InlineData(TenantWrightDatabaseCatalog.Tenant)]
    [InlineData(TenantWrightDatabaseCatalog.Workflows)]
    [InlineData("UnreviewedName")]
    public async Task Host_only_names_ignore_tenant_values_and_resolve_the_host_database(string name)
    {
        var resolver = CreateResolver(TenantId, Tenant(connectionStrings: (name, Dedicated)), allowHostFallback: false);

        Assert.Equal(HostDefault, await resolver.ResolveAsync(name));
    }

    [Fact]
    public async Task Inactive_tenant_fails_closed_for_every_name_even_with_fallback()
    {
        var resolver = CreateResolver(
            TenantId,
            Tenant(isActive: false, connectionStrings: (TenantWrightDatabaseCatalog.Notifications, Dedicated)),
            allowHostFallback: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(TenantWrightDatabaseCatalog.Notifications));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(TenantWrightDatabaseCatalog.Identity));
    }

    [Fact]
    public async Task Unknown_tenant_fails_closed_even_with_fallback()
    {
        var resolver = CreateResolver(TenantId, tenant: null, allowHostFallback: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(TenantWrightDatabaseCatalog.Notifications));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(TenantWrightDatabaseCatalog.Identity));
    }

    [Fact]
    public async Task Dedicated_database_targeting_the_shared_database_fails_closed()
    {
        // Same host/port/database as the shared one, different credentials: still the shared database.
        var resolver = CreateResolver(
            TenantId,
            Tenant(connectionStrings: (TenantWrightDatabaseCatalog.Notifications, "Host=SHARED-DB;Port=5432;Database=tenantwright;Username=other")),
            allowHostFallback: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(TenantWrightDatabaseCatalog.Notifications));
    }

    [Fact]
    public async Task Unknown_tenant_is_not_pinned_in_process_so_a_repopulated_cache_is_seen_immediately()
    {
        var store = new MutableTenantStore();
        var resolver = CreateResolver(TenantId, store, new TenantConnectionStringsCache(
            new TestOptionsMonitor<SharedConnectionStringResolverOptions>(
                new SharedConnectionStringResolverOptions { TenantCacheSeconds = 300 })));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(TenantWrightDatabaseCatalog.Notifications));

        store.Tenant = Tenant(connectionStrings: (TenantWrightDatabaseCatalog.Notifications, Dedicated));

        Assert.Equal(Dedicated, await resolver.ResolveAsync(TenantWrightDatabaseCatalog.Notifications));
    }

    [Fact]
    public async Task Tenant_lookups_are_cached_per_tenant()
    {
        var store = new MutableTenantStore { Tenant = Tenant(connectionStrings: (TenantWrightDatabaseCatalog.Notifications, Dedicated)) };
        var resolver = CreateResolver(TenantId, store, new TenantConnectionStringsCache(
            new TestOptionsMonitor<SharedConnectionStringResolverOptions>(
                new SharedConnectionStringResolverOptions { TenantCacheSeconds = 300 })));

        await resolver.ResolveAsync(TenantWrightDatabaseCatalog.Notifications);
        await resolver.ResolveAsync(TenantWrightDatabaseCatalog.Notifications);

        Assert.Equal(1, store.FindByIdCalls);
    }

    private static TenantConfiguration Tenant(
        bool isActive = true,
        params (string Name, string Value)[] connectionStrings)
    {
        var tenant = new TenantConfiguration
        {
            Id = TenantId,
            Name = "acme",
            NormalizedName = "ACME",
            IsActive = isActive
        };
        foreach (var (name, value) in connectionStrings)
        {
            tenant.ConnectionStrings[name] = value;
        }

        return tenant;
    }

    private static SharedDbConnectionOptions HostOptions()
    {
        var options = new SharedDbConnectionOptions();
        options.ConnectionStrings.Default = HostDefault;
        return options;
    }

    private static TenantConnectionStringResolver CreateResolver(
        Guid? currentTenantId,
        TenantConfiguration? tenant,
        bool allowHostFallback = false,
        SharedDbConnectionOptions? hostOptions = null) =>
        CreateResolver(
            currentTenantId,
            new MutableTenantStore { Tenant = tenant },
            new TenantConnectionStringsCache(
                new TestOptionsMonitor<SharedConnectionStringResolverOptions>(new SharedConnectionStringResolverOptions())),
            allowHostFallback,
            hostOptions);

    private static TenantConnectionStringResolver CreateResolver(
        Guid? currentTenantId,
        ITenantStore store,
        ITenantConnectionStringsCache cache,
        bool allowHostFallback = false,
        SharedDbConnectionOptions? hostOptions = null) =>
        new(
            currentTenant: new TestCurrentTenant(currentTenantId),
            tenantStore: store,
            tenantConnectionStringsCache: cache,
            tenantConnectionStringProtector: new NoopTenantConnectionStringProtector(),
            options: new TestOptionsMonitor<SharedDbConnectionOptions>(hostOptions ?? HostOptions()),
            resolverOptions: new TestOptionsMonitor<SharedConnectionStringResolverOptions>(
                new SharedConnectionStringResolverOptions { AllowHostFallbackForTenant = allowHostFallback }),
            logger: NullLogger<TenantConnectionStringResolver>.Instance);

    private sealed class MutableTenantStore : ITenantStore
    {
        public TenantConfiguration? Tenant { get; set; }

        public int FindByIdCalls { get; private set; }

        public TenantConfiguration? Find(Guid id) => Tenant?.Id == id ? Tenant : null;

        public TenantConfiguration? Find(string normalizedName) => Tenant?.NormalizedName == normalizedName ? Tenant : null;

        public IReadOnlyList<TenantConfiguration> GetList() => Tenant == null ? [] : [Tenant];

        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
        {
            FindByIdCalls++;
            return Task.FromResult(Tenant?.Id == id ? Tenant : null);
        }

        public Task<TenantConfiguration?> FindAsync(string normalizedName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Tenant?.NormalizedName == normalizedName ? Tenant : null);

        public async Task<IReadOnlyList<TenantConfiguration>?> FindListAsync(CancellationToken cancellationToken = default) =>
            await GetListAsync(cancellationToken);

        public Task<IReadOnlyList<TenantConfiguration>> GetListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantConfiguration>>(Tenant == null ? [] : [Tenant]);
    }

    private sealed class NoopTenantConnectionStringProtector : ITenantConnectionStringProtector
    {
        public string Protect(Guid tenantId, string connectionName, string plaintext) => plaintext;

        public string Unprotect(Guid tenantId, string connectionName, string storedValue) => storedValue;

        public bool IsProtected(string? storedValue) => false;
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public bool IsAvailable => Id.HasValue;

        public Guid? Id { get; private set; }

        public string? Name => null;

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previous = Id;
            Id = id;
            return new RestoreTenant(this, previous);
        }

        private sealed class RestoreTenant(TestCurrentTenant owner, Guid? previous) : IDisposable
        {
            public void Dispose() => owner.Id = previous;
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class, new()
    {
        public TestOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
