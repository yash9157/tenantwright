using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.Caching.Abstractions;
using Shared.Data.Filtering;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore;
using Shared.MultiTenancy;
using Shared.Modularity.DependencyInjection;
using Shared.Permissions.Definitions.Tenant;
using Shared.Threading;
using Shared.Uow.Options;
using Tenant.Application.Contracts.Dashboard;
using Tenant.Application.Dashboard;
using Tenant.Controllers.CurrentTenantDashboard;
using Tenant.Controllers.Dashboard;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;
using Xunit;

namespace Tenant.Tests;

public class TenantDashboardAppServiceTests
{
    [Fact]
    public async Task GetAdminAsync_should_return_tenant_inventory_summary()
    {
        var now = DateTime.UtcNow;
        var hostTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(hostTenant);

        var tenantA = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "north-org")
        {
            CreationTime = now.AddDays(-1)
        };
        var tenantB = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "south-org");
        tenantB.Deactivate();
        tenantB.CreationTime = now.AddDays(-3);
        var tenantC = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "east-org")
        {
            CreationTime = now.AddDays(-40)
        };

        dbContext.Tenants.AddRange(tenantA, tenantB, tenantC);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var service = new TenantDashboardAppService(
            CurrentTenantProfileTests.CreateTenantRepository(dbContext),
            CurrentTenantProfileTests.CreateOwnershipRepository(dbContext),
            CurrentTenantProfileTests.CreateTransitionRepository(dbContext),
            hostTenant,
            new InMemoryHybridCache<AdminTenantDashboardDto>(),
            new InMemoryHybridCache<TenantOnboardingFunnelDto>(),
            new InMemoryHybridCache<CurrentTenantDashboardDto>());

        var dashboard = await service.GetAdminAsync(30);

        Assert.Equal(30, dashboard.WindowDays);
        Assert.Equal(3, dashboard.TotalTenants);
        Assert.Equal(2, dashboard.ActiveTenants);
        Assert.Equal(1, dashboard.InactiveTenants);
        Assert.Equal(2, dashboard.NewTenantsInWindow);
        Assert.Equal(30, dashboard.TenantCreationTrend.Count);
        Assert.Equal(3, dashboard.RecentTenants.Count);
        Assert.Equal("north-org", dashboard.RecentTenants[0].Name);

        Assert.NotEqual(default, dashboard.GeneratedAtUtc);
        Assert.Equal(2, dashboard.LifecycleDistribution
            .Single(x => x.LifecycleStatus == global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Active).Count);
        Assert.Equal(1, dashboard.LifecycleDistribution
            .Single(x => x.LifecycleStatus == global::Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended).Count);
    }

    [Fact]
    public async Task GetAdminAsync_should_build_zero_filled_cumulative_daily_trend()
    {
        var now = DateTime.UtcNow;
        var hostTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(hostTenant);

        // Two tenants inside the 30-day window and one that predates it: the baseline
        // must lift the cumulative series and the trailing point must equal the total.
        var inWindowA = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "north-org") { CreationTime = now.AddDays(-2) };
        var inWindowB = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "south-org") { CreationTime = now.AddDays(-2) };
        var priorTenant = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "east-org") { CreationTime = now.AddDays(-60) };
        dbContext.Tenants.AddRange(inWindowA, inWindowB, priorTenant);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var service = new TenantDashboardAppService(
            CurrentTenantProfileTests.CreateTenantRepository(dbContext),
            CurrentTenantProfileTests.CreateOwnershipRepository(dbContext),
            CurrentTenantProfileTests.CreateTransitionRepository(dbContext),
            hostTenant,
            new InMemoryHybridCache<AdminTenantDashboardDto>(),
            new InMemoryHybridCache<TenantOnboardingFunnelDto>(),
            new InMemoryHybridCache<CurrentTenantDashboardDto>());

        var dashboard = await service.GetAdminAsync(30);

        Assert.Equal("day", dashboard.Granularity);
        Assert.Equal(30, dashboard.TenantCreationTrend.Count);

        // Contiguous, zero-filled daily buckets: each bucket ends where the next starts.
        for (var i = 1; i < dashboard.TenantCreationTrend.Count; i++)
        {
            Assert.Equal(dashboard.TenantCreationTrend[i - 1].BucketEndUtc, dashboard.TenantCreationTrend[i].BucketStartUtc);
            Assert.Equal(1, (dashboard.TenantCreationTrend[i].BucketStartUtc - dashboard.TenantCreationTrend[i - 1].BucketStartUtc).TotalDays);
        }

        // Cumulative is monotonic non-decreasing and starts from the pre-window baseline (1).
        Assert.Equal(1, dashboard.TenantCreationTrend[0].CumulativeTotal - dashboard.TenantCreationTrend[0].CreatedCount);
        Assert.All(
            dashboard.TenantCreationTrend.Zip(dashboard.TenantCreationTrend.Skip(1)),
            pair => Assert.True(pair.Second.CumulativeTotal >= pair.First.CumulativeTotal));
        Assert.Equal(dashboard.TotalTenants, dashboard.TenantCreationTrend[^1].CumulativeTotal);
        Assert.Equal(2, dashboard.TenantCreationTrend.Sum(x => x.CreatedCount));
    }

    [Fact]
    public async Task GetAdminAsync_should_roll_up_to_weekly_buckets_for_ninety_day_window()
    {
        var now = DateTime.UtcNow;
        var hostTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(hostTenant);
        dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "north-org") { CreationTime = now.AddDays(-10) });
        await dbContext.SaveChangesOnDbContextAsync(true);

        var service = new TenantDashboardAppService(
            CurrentTenantProfileTests.CreateTenantRepository(dbContext),
            CurrentTenantProfileTests.CreateOwnershipRepository(dbContext),
            CurrentTenantProfileTests.CreateTransitionRepository(dbContext),
            hostTenant,
            new InMemoryHybridCache<AdminTenantDashboardDto>(),
            new InMemoryHybridCache<TenantOnboardingFunnelDto>(),
            new InMemoryHybridCache<CurrentTenantDashboardDto>());

        var dashboard = await service.GetAdminAsync(90);

        Assert.Equal("week", dashboard.Granularity);
        // ceil(90 / 7) = 13 contiguous buckets, the last one clamped to the window end.
        Assert.Equal(13, dashboard.TenantCreationTrend.Count);
        Assert.All(
            dashboard.TenantCreationTrend.Take(12),
            bucket => Assert.Equal(7, (bucket.BucketEndUtc - bucket.BucketStartUtc).TotalDays));
        Assert.Equal(dashboard.TotalTenants, dashboard.TenantCreationTrend[^1].CumulativeTotal);
    }

    [Fact]
    public async Task GetOnboardingFunnelAsync_should_build_monotonic_stages_with_stall_detection()
    {
        var now = DateTime.UtcNow;
        var hostTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(hostTenant);

        // Fully onboarded + live tenant: reaches every stage.
        var activeTenantId = Guid.NewGuid();
        var activeTenant = new global::Tenant.Domain.Tenants.Tenant(activeTenantId, "north-org")
        {
            CreationTime = now.AddDays(-2)
        };
        var ownerId = Guid.NewGuid();
        var activeRequest = new global::Tenant.Domain.Tenants.TenantOwnershipRequest(
            Guid.NewGuid(), activeTenantId, null, "owner@north.example", null, "initial-owner", null,
            global::Tenant.Domain.Shared.Enums.TenantOwnershipRequestKind.InitialOwner)
        {
            CreationTime = now.AddDays(-2)
        };
        activeRequest.AcknowledgeIdentityInvitation(Guid.NewGuid(), now.AddDays(-2).AddHours(1));
        activeRequest.Complete(ownerId, now.AddDays(-1));
        activeRequest.AcknowledgePermissionsProvisioned(ownerId, now.AddDays(-1).AddMinutes(5));

        // Stuck past the 48h stall threshold at "invitation sent".
        var stuckTenantId = Guid.NewGuid();
        var stuckTenant = new global::Tenant.Domain.Tenants.Tenant(stuckTenantId, "south-org")
        {
            CreationTime = now.AddDays(-5)
        };
        stuckTenant.BeginProvisioning();
        var stuckRequest = new global::Tenant.Domain.Tenants.TenantOwnershipRequest(
            Guid.NewGuid(), stuckTenantId, null, "owner@south.example", null, "initial-owner", null,
            global::Tenant.Domain.Shared.Enums.TenantOwnershipRequestKind.InitialOwner)
        {
            CreationTime = now.AddDays(-5)
        };
        stuckRequest.AcknowledgeIdentityInvitation(Guid.NewGuid(), now.AddDays(-5).AddHours(1));

        dbContext.Tenants.AddRange(activeTenant, stuckTenant);
        dbContext.TenantOwnershipRequests.AddRange(activeRequest, stuckRequest);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var service = new TenantDashboardAppService(
            CurrentTenantProfileTests.CreateTenantRepository(dbContext),
            CurrentTenantProfileTests.CreateOwnershipRepository(dbContext),
            CurrentTenantProfileTests.CreateTransitionRepository(dbContext),
            hostTenant,
            new InMemoryHybridCache<AdminTenantDashboardDto>(),
            new InMemoryHybridCache<TenantOnboardingFunnelDto>(),
            new InMemoryHybridCache<CurrentTenantDashboardDto>());

        var funnel = await service.GetOnboardingFunnelAsync(30, 48);

        int StageCount(string key) => funnel.Stages.Single(s => s.Key == key).Count;
        Assert.Equal(48, funnel.StallThresholdHours);
        Assert.Equal(2, StageCount("created"));
        Assert.Equal(2, StageCount("invitationSent"));
        Assert.Equal(1, StageCount("ownerVerified"));
        Assert.Equal(1, StageCount("permissionsProvisioned"));
        Assert.Equal(1, StageCount("active"));

        // Counts never increase down the funnel.
        for (var i = 1; i < funnel.Stages.Count; i++)
        {
            Assert.True(funnel.Stages[i].Count <= funnel.Stages[i - 1].Count);
        }

        // The stuck tenant is flagged at invitation sent; nobody is stalled at created.
        Assert.Equal(1, funnel.Stages.Single(s => s.Key == "invitationSent").StalledCount);
        Assert.Equal(0, funnel.Stages.Single(s => s.Key == "created").StalledCount);
    }

    [Fact]
    public async Task GetOnboardingFunnelAsync_should_fail_closed_for_invalid_stall_threshold()
    {
        var hostTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(hostTenant);
        var service = new TenantDashboardAppService(
            CurrentTenantProfileTests.CreateTenantRepository(dbContext),
            CurrentTenantProfileTests.CreateOwnershipRepository(dbContext),
            CurrentTenantProfileTests.CreateTransitionRepository(dbContext),
            hostTenant,
            new InMemoryHybridCache<AdminTenantDashboardDto>(),
            new InMemoryHybridCache<TenantOnboardingFunnelDto>(),
            new InMemoryHybridCache<CurrentTenantDashboardDto>());

        await Assert.ThrowsAsync<SharedValidationException>(() => service.GetOnboardingFunnelAsync(30, 0));
    }

    [Fact]
    public async Task GetAdminAsync_should_surface_needs_attention_counts()
    {
        var now = DateTime.UtcNow;
        var hostTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(hostTenant);

        // Active tenant with no connection string configured -> missing connection.
        var activeTenant = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "north-org")
        {
            CreationTime = now.AddDays(-1)
        };
        // Inactive tenant created well beyond the chronic threshold, with no transition
        // row -> counts as suspended over the threshold via the creation-time fallback.
        var chronicTenant = new global::Tenant.Domain.Tenants.Tenant(Guid.NewGuid(), "south-org")
        {
            CreationTime = now.AddDays(-40)
        };
        chronicTenant.Deactivate();
        dbContext.Tenants.AddRange(activeTenant, chronicTenant);

        // Initial-owner provisioning request stuck past the 48h stall threshold.
        var stuckRequest = new global::Tenant.Domain.Tenants.TenantOwnershipRequest(
            Guid.NewGuid(), Guid.NewGuid(), null, "owner@stuck.example", null, "initial-owner", null,
            global::Tenant.Domain.Shared.Enums.TenantOwnershipRequestKind.InitialOwner)
        {
            CreationTime = now.AddDays(-3)
        };
        dbContext.TenantOwnershipRequests.Add(stuckRequest);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var service = new TenantDashboardAppService(
            CurrentTenantProfileTests.CreateTenantRepository(dbContext),
            CurrentTenantProfileTests.CreateOwnershipRepository(dbContext),
            CurrentTenantProfileTests.CreateTransitionRepository(dbContext),
            hostTenant,
            new InMemoryHybridCache<AdminTenantDashboardDto>(),
            new InMemoryHybridCache<TenantOnboardingFunnelDto>(),
            new InMemoryHybridCache<CurrentTenantDashboardDto>());

        var dashboard = await service.GetAdminAsync(30);

        Assert.Equal(1, dashboard.Attention.StalledProvisioning);
        Assert.Equal(1, dashboard.Attention.SuspendedOverThirtyDays);
    }

    [Fact]
    public async Task GetCurrentAsync_should_return_safe_current_tenant_dashboard()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(tenantId);
        await using var dbContext = CreateDbContext(currentTenant);
        var tenant = new global::Tenant.Domain.Tenants.Tenant(tenantId, "north-org")
        {
            CreationTime = DateTime.UtcNow.AddDays(-10)
        };
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var service = new TenantDashboardAppService(
            CurrentTenantProfileTests.CreateTenantRepository(dbContext),
            CurrentTenantProfileTests.CreateOwnershipRepository(dbContext),
            CurrentTenantProfileTests.CreateTransitionRepository(dbContext),
            currentTenant,
            new InMemoryHybridCache<AdminTenantDashboardDto>(),
            new InMemoryHybridCache<TenantOnboardingFunnelDto>(),
            new InMemoryHybridCache<CurrentTenantDashboardDto>());

        var dashboard = await service.GetCurrentAsync();

        Assert.Equal(tenantId, dashboard.TenantId);
        Assert.Equal("north-org", dashboard.Name);
        Assert.True(dashboard.IsActive);
        Assert.NotEqual(default, dashboard.CreatedAtUtc);
    }

    [Fact]
    public void Controllers_should_expose_dashboard_routes_with_dashboard_permissions()
    {
        var adminAuthorize = typeof(DashboardController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        var adminRoute = typeof(DashboardController).GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Single();
        var currentAuthorize = typeof(CurrentTenantDashboardController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        var currentRoute = typeof(CurrentTenantDashboardController).GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Single();

        Assert.Equal(TenantPermissions.Dashboard.Default, adminAuthorize.Policy);
        Assert.Equal("api/tenant/dashboard", adminRoute.Template);
        Assert.Equal(TenantPermissions.Dashboard.Tenant, currentAuthorize.Policy);
        Assert.Equal("api/tenant/current", currentRoute.Template);
        Assert.NotNull(typeof(DashboardController).GetMethod(nameof(DashboardController.GetAdminAsync))!
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
            .Cast<HttpGetAttribute>()
            .SingleOrDefault(x => x.Template == "admin"));
        Assert.NotNull(typeof(CurrentTenantDashboardController).GetMethod(nameof(CurrentTenantDashboardController.GetDashboardAsync))!
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
            .Cast<HttpGetAttribute>()
            .SingleOrDefault(x => x.Template == "dashboard"));
    }

    [Fact]
    public async Task GetAdminAsync_should_fail_closed_for_invalid_window_days()
    {
        var currentTenant = new TestCurrentTenant(null);
        await using var dbContext = CreateDbContext(currentTenant);
        var service = new TenantDashboardAppService(
            CurrentTenantProfileTests.CreateTenantRepository(dbContext),
            CurrentTenantProfileTests.CreateOwnershipRepository(dbContext),
            CurrentTenantProfileTests.CreateTransitionRepository(dbContext),
            currentTenant,
            new InMemoryHybridCache<AdminTenantDashboardDto>(),
            new InMemoryHybridCache<TenantOnboardingFunnelDto>(),
            new InMemoryHybridCache<CurrentTenantDashboardDto>());

        await Assert.ThrowsAsync<SharedValidationException>(() => service.GetAdminAsync(14));
    }

    private static TenantDbContextApp CreateDbContext(TestCurrentTenant currentTenant)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentTenant>(currentTenant);
        services.AddSingleton<IDataFilter, TestDataFilter>();
        services.AddSingleton<IAuditPropertySetter, NoopAuditPropertySetter>();
        services.AddSingleton<IAuditingManager, NoopAuditingManager>();
        services.AddSingleton<Shared.Uow.Abstractions.IUnitOfWorkManager, NoopUnitOfWorkManager>();
        services.AddSingleton<ICancellationTokenProvider, TestCancellationTokenProvider>();
        services.AddSingleton<IClock, UtcClock>();
        var provider = services.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<TenantDbContextApp>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        var dbContext = new TenantDbContextApp(options)
        {
            ServiceProvider = provider
        };
        dbContext.Initialize(new EfCoreDbContextInitializationContext(new TestUnitOfWork()));
        return dbContext;
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
            var previousId = Id;
            Id = id;
            return new DisposeAction(() => Id = previousId);
        }
    }

    private sealed class TestDataFilter : IDataFilter
    {
        public IDisposable Enable<TFilter>() where TFilter : class => new DisposeAction(() => { });
        public IDisposable Disable<TFilter>() where TFilter : class => new DisposeAction(() => { });
        public bool IsEnabled<TFilter>() where TFilter : class => true;
    }

    private sealed class DisposeAction : IDisposable
    {
        private readonly Action _dispose;

        public DisposeAction(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            _dispose();
        }
    }

    private sealed class TestCancellationTokenProvider : ICancellationTokenProvider
    {
        public CancellationToken Token => CancellationToken.None;
    }

    private sealed class InMemoryHybridCache<TCacheItem> : IHybridCache<TCacheItem, string>
        where TCacheItem : class
    {
        private readonly Dictionary<string, TCacheItem> _items = new(StringComparer.Ordinal);

        public Task<TCacheItem?> GetOrCreateAsync(
            string key,
            Func<Task<TCacheItem>> factory,
            Func<HybridCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            if (_items.TryGetValue(key, out var cached))
            {
                return Task.FromResult<TCacheItem?>(cached);
            }

            return CreateAsync(key, factory);
        }

        public Task SetAsync(
            string key,
            TCacheItem value,
            HybridCacheEntryOptions? options = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            _items[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            string key,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            _items.Remove(key);
            return Task.CompletedTask;
        }

        public Task RemoveManyAsync(
            IEnumerable<string> keys,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            foreach (var key in keys)
            {
                _items.Remove(key);
            }

            return Task.CompletedTask;
        }

        private async Task<TCacheItem?> CreateAsync(string key, Func<Task<TCacheItem>> factory)
        {
            var created = await factory();
            _items[key] = created;
            return created;
        }
    }

    private sealed class NoopAuditPropertySetter : IAuditPropertySetter
    {
        public void SetCreationProperties(object targetObject) { }
        public void SetDeletionProperties(object targetObject) { }
        public void SetModificationProperties(object targetObject) { }
        public void IncrementEntityVersionProperty(object entity) { }
    }

    private sealed class NoopAuditingManager : IAuditingManager
    {
        public IAuditLogScope? Current => null;
        public IAuditLogSaveHandle BeginScope() => new NoopAuditLogScope();

        private sealed class NoopAuditLogScope : IAuditLogScope, IAuditLogSaveHandle
        {
            public AuditLogInfo Log { get; } = new();
            public Task SaveAsync() => Task.CompletedTask;
            public void Dispose() { }
        }
    }

    private sealed class NoopUnitOfWorkManager : Shared.Uow.Abstractions.IUnitOfWorkManager
    {
        public Shared.Uow.Abstractions.IUnitOfWork? Current => null;
        public Shared.Uow.Abstractions.IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Begin(bool requiresNew = false) => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => throw new NotSupportedException();
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) => throw new NotSupportedException();
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => false;
    }

    private sealed class TestUnitOfWork : Shared.Uow.Abstractions.IUnitOfWork
    {
        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public event EventHandler<Shared.Uow.Events.UnitOfWorkFailedEventArgs>? Failed { add { } remove { } }
        public event EventHandler<Shared.Uow.Events.UnitOfWorkEventArgs>? Disposed;
        public UnitOfWorkOptions Options { get; } = new();
        public Shared.Uow.Abstractions.IUnitOfWork? Outer => null;
        public bool IsReserved => false;
        public bool IsDisposed { get; private set; }
        public bool IsCompleted { get; private set; }
        public string? ReservationName => null;
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public void SetOuter(Shared.Uow.Abstractions.IUnitOfWork? outer) { }
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
        public Shared.Uow.Abstractions.IDatabaseApi? FindDatabaseApi(string key) => null;
        public void AddDatabaseApi(string key, Shared.Uow.Abstractions.IDatabaseApi api) { }
        public Shared.Uow.Abstractions.IDatabaseApi GetOrAddDatabaseApi(string key, Func<Shared.Uow.Abstractions.IDatabaseApi> factory) => factory();
        public Shared.Uow.Abstractions.ITransactionApi? FindTransactionApi(string key) => null;
        public void AddTransactionApi(string key, Shared.Uow.Abstractions.ITransactionApi api) { }
        public Shared.Uow.Abstractions.ITransactionApi GetOrAddTransactionApi(string key, Func<Shared.Uow.Abstractions.ITransactionApi> factory) => factory();
        public void Dispose()
        {
            IsDisposed = true;
            Disposed?.Invoke(this, new Shared.Uow.Events.UnitOfWorkEventArgs(this));
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

}
