using Shared.MultiTenancy;
using Shared.Data.Filtering;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Permissions.Application.Caching;
using Permissions.Application.Evaluation;
using Permissions.Application.Grants;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.Tests.TestDoubles;
using Xunit;

namespace Permissions.Tests;

/// <summary>
/// Coverage for sub-flow 3 (grant lifecycle metadata, time-bound grants, and the expiry sweep).
/// Runs on SQLite (relational) because the sweep uses <c>ExecuteDelete</c>/<c>IgnoreQueryFilters</c>,
/// which the EF Core InMemory provider does not support.
/// </summary>
public sealed class PermissionGrantLifecycleTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public void PermissionGrant_round_trips_lifecycle_metadata()
    {
        var grantedBy = Guid.NewGuid();
        var expiresAt = DateTime.UtcNow.AddDays(7);

        var grant = new PermissionGrant(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            name: "Documents.Read",
            providerName: PermissionProviderNames.User,
            providerKey: Guid.NewGuid().ToString("D"),
            effect: PermissionGrantEffect.Allow,
            creationTimeUtc: DateTime.UtcNow,
            expiresAtUtc: expiresAt,
            reason: "Temporary contractor access",
            ticketId: "CHG-1234",
            grantedByUserId: grantedBy);

        Assert.Equal(expiresAt, grant.ExpiresAtUtc);
        Assert.Equal("Temporary contractor access", grant.Reason);
        Assert.Equal("CHG-1234", grant.TicketId);
        Assert.Equal(grantedBy, grant.GrantedByUserId);
        Assert.False(grant.IsExpired(DateTime.UtcNow));
        Assert.True(grant.IsExpired(expiresAt.AddSeconds(1)));
    }

    [Fact]
    public void SetLifecycleMetadata_updates_all_fields_and_clears_blank_reason_and_ticket()
    {
        var grant = new PermissionGrant(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            name: "Documents.Read",
            providerName: PermissionProviderNames.User,
            providerKey: Guid.NewGuid().ToString("D"),
            effect: PermissionGrantEffect.Allow,
            creationTimeUtc: DateTime.UtcNow,
            reason: "Initial reason",
            ticketId: "CHG-0001");

        grant.SetLifecycleMetadata(expiresAtUtc: null, reason: "  ", ticketId: "  ", grantedByUserId: null);

        Assert.Null(grant.ExpiresAtUtc);
        Assert.Null(grant.Reason);
        Assert.Null(grant.TicketId);
        Assert.Null(grant.GrantedByUserId);
    }

    [Fact]
    public async Task Snapshot_builder_excludes_expired_grants_from_the_evaluation_result()
    {
        using var connection = OpenDatabase();
        var userId = Guid.NewGuid();

        await SeedGrantsAsync(connection,
            NewGrant(userId, "Documents.Read", expiresAtUtc: DateTime.UtcNow.AddMinutes(-5)), // expired
            NewGrant(userId, "Documents.Update", expiresAtUtc: DateTime.UtcNow.AddDays(1))); // active

        var (snapshot, _) = await BuildSnapshotAsync(connection, userId);
        var engine = new PermissionDecisionEngine();

        var expiredDecision = engine.Evaluate(snapshot, "Documents.Read");
        var activeDecision = engine.Evaluate(snapshot, "Documents.Update");

        Assert.False(expiredDecision.IsGranted);
        Assert.Equal(PermissionDecisionSource.NotGranted, expiredDecision.Source);
        Assert.True(activeDecision.IsGranted);
    }

    [Fact]
    public async Task Snapshot_builder_includes_grants_with_no_expiry()
    {
        using var connection = OpenDatabase();
        var userId = Guid.NewGuid();

        await SeedGrantsAsync(connection, NewGrant(userId, "Documents.Read", expiresAtUtc: null));

        var (snapshot, _) = await BuildSnapshotAsync(connection, userId);
        var decision = new PermissionDecisionEngine().Evaluate(snapshot, "Documents.Read");

        Assert.True(decision.IsGranted);
    }

    [Fact]
    public async Task Sweep_removes_expired_grants_and_leaves_active_ones()
    {
        using var connection = OpenDatabase();
        var userId = Guid.NewGuid();

        await SeedGrantsAsync(connection,
            NewGrant(userId, "Documents.Read", expiresAtUtc: DateTime.UtcNow.AddMinutes(-10)),
            NewGrant(userId, "Documents.Delete", expiresAtUtc: DateTime.UtcNow.AddMinutes(-1)),
            NewGrant(userId, "Documents.Update", expiresAtUtc: DateTime.UtcNow.AddDays(1)),
            NewGrant(userId, "Documents.Create", expiresAtUtc: null));

        var removed = await RunSweepAsync(connection);

        Assert.Equal(2, removed);
        var remaining = await CountGrantsAsync(connection);
        Assert.Equal(2, remaining);
    }

    [Fact]
    public async Task Sweep_bumps_the_stamp_for_a_provider_whose_grant_expired()
    {
        using var connection = OpenDatabase();
        var userId = Guid.NewGuid();
        var providerKey = userId.ToString("D");

        await SeedGrantsAsync(connection, NewGrant(userId, "Documents.Read", expiresAtUtc: DateTime.UtcNow.AddMinutes(-1)));

        var cacheServiceProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheServiceProvider);
        var stampBefore = await stampService.GetAsync(TenantId, PermissionProviderNames.User, providerKey);

        await RunSweepAsync(connection, cacheServiceProvider);

        var stampAfter = await stampService.GetAsync(TenantId, PermissionProviderNames.User, providerKey);
        Assert.True(stampAfter > stampBefore, $"Expected stamp to increase past {stampBefore}, got {stampAfter}.");
    }

    [Fact]
    public async Task Sweep_is_a_noop_when_nothing_has_expired()
    {
        using var connection = OpenDatabase();
        var userId = Guid.NewGuid();
        await SeedGrantsAsync(connection, NewGrant(userId, "Documents.Read", expiresAtUtc: DateTime.UtcNow.AddDays(1)));

        var removed = await RunSweepAsync(connection);

        Assert.Equal(0, removed);
        Assert.Equal(1, await CountGrantsAsync(connection));
    }

    [Fact]
    public async Task Host_scoped_cache_refresh_rebuilds_every_tenant_provider()
    {
        // Regression: the recurring refresh runs without an ambient tenant. Before the fix it read
        // grants through the tenant filter, saw only host providers, and let tenant role entries
        // expire, so every tenant request was denied once the startup-written cache aged out.
        using var connection = OpenDatabase();
        var tenantRoleKey = Guid.NewGuid().ToString("D");
        var hostRoleKey = Guid.NewGuid().ToString("D");
        await SeedGrantsAsync(
            connection,
            NewRoleGrant(TenantId, tenantRoleKey, "Documents.Read"),
            NewRoleGrant(null, hostRoleKey, "Platform.Read"));

        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null, multiTenantFilterEnabled: true);
        var cacheServiceProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var grantRepository = new EfTestRepository<PermissionGrant, Guid>(context);
        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheServiceProvider);
        var grantsCache = ActivatorUtilities.CreateInstance<PermissionProviderGrantsCache>(cacheServiceProvider);
        var grantCacheSync = new PermissionProviderGrantCacheSyncService(
            grantRepository,
            grantsCache,
            cacheServiceProvider.GetRequiredService<RolePrivilegeMatrixCache>());
        var currentTenant = context.ServiceProvider.GetRequiredService<ICurrentTenant>();
        var repairService = new PermissionProviderCacheRepairService(
            grantRepository,
            grantCacheSync,
            stampService,
            context.ServiceProvider.GetRequiredService<IDataFilter>(),
            currentTenant,
            NullLogger<PermissionProviderCacheRepairService>.Instance);

        await repairService.RefreshAllProvidersAsync();

        var tenantStamp = await stampService.GetAsync(TenantId, PermissionProviderNames.Role, tenantRoleKey);
        var hostStamp = await stampService.GetAsync(null, PermissionProviderNames.Role, hostRoleKey);
        Assert.True(tenantStamp > 0);
        Assert.True(hostStamp > 0);
        Assert.Null(currentTenant.Id);

        // A miss would mean the refresh never wrote the tenant entry; fail instead of repopulating it.
        var tenantGrants = await grantsCache.GetOrCreateAsync(
            PermissionCacheKeys.BuildProviderGrantsKey(TenantId, PermissionProviderNames.Role, tenantRoleKey, tenantStamp),
            () => throw new Xunit.Sdk.XunitException("Tenant role grants were not written by the host-scoped refresh."));
        Assert.NotNull(tenantGrants);
        Assert.Equal(new[] { "Documents.Read" }, tenantGrants!.AllowExact);
    }

    private static PermissionGrant NewRoleGrant(Guid? tenantId, string roleKey, string name)
    {
        return new PermissionGrant(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            name: name,
            providerName: PermissionProviderNames.Role,
            providerKey: roleKey,
            effect: PermissionGrantEffect.Allow,
            creationTimeUtc: DateTime.UtcNow,
            expiresAtUtc: null);
    }

    private static PermissionGrant NewGrant(Guid userId, string name, DateTime? expiresAtUtc)
    {
        return new PermissionGrant(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            name: name,
            providerName: PermissionProviderNames.User,
            providerKey: userId.ToString("D"),
            effect: PermissionGrantEffect.Allow,
            creationTimeUtc: DateTime.UtcNow,
            expiresAtUtc: expiresAtUtc);
    }

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        connection.Open();
        using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null, multiTenantFilterEnabled: true);
        context.Database.EnsureCreated();
        return connection;
    }

    private static async Task SeedGrantsAsync(SqliteConnection connection, params PermissionGrant[] grants)
    {
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null, multiTenantFilterEnabled: true);
        context.PermissionGrants.AddRange(grants);
        await context.SaveChangesAsync();
    }

    private static async Task<int> CountGrantsAsync(SqliteConnection connection)
    {
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null, multiTenantFilterEnabled: true);
        return context.PermissionGrants.IgnoreQueryFilters().Count();
    }

    private static async Task<(PermissionSnapshotCacheItem Snapshot, string SnapshotHash)> BuildSnapshotAsync(
        SqliteConnection connection,
        Guid userId)
    {
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true);
        var cacheServiceProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var grantRepository = new EfTestRepository<PermissionGrant, Guid>(context);
        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheServiceProvider);
        var grantsCache = ActivatorUtilities.CreateInstance<PermissionProviderGrantsCache>(cacheServiceProvider);
        var grantCacheSync = new PermissionProviderGrantCacheSyncService(
            grantRepository,
            grantsCache,
            cacheServiceProvider.GetRequiredService<RolePrivilegeMatrixCache>());
        var repairService = new PermissionProviderCacheRepairService(grantRepository, grantCacheSync, stampService, context.ServiceProvider.GetRequiredService<IDataFilter>(), context.ServiceProvider.GetRequiredService<ICurrentTenant>(), NullLogger<PermissionProviderCacheRepairService>.Instance);
        var snapshotCache = ActivatorUtilities.CreateInstance<PermissionSnapshotCache>(cacheServiceProvider);
        var builder = new PermissionSnapshotBuilder(grantRepository, repairService, snapshotCache, NullLogger<PermissionSnapshotBuilder>.Instance);

        return await builder.GetOrBuildAsync(TenantId, userId, []);
    }

    private static Task<int> RunSweepAsync(SqliteConnection connection)
        => RunSweepAsync(connection, PermissionsCachingTestHarness.BuildServiceProvider());

    private static async Task<int> RunSweepAsync(SqliteConnection connection, IServiceProvider cacheServiceProvider)
    {
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null, multiTenantFilterEnabled: true);
        var grantRepository = new EfTestRepository<PermissionGrant, Guid>(context);
        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheServiceProvider);
        var grantsCache = ActivatorUtilities.CreateInstance<PermissionProviderGrantsCache>(cacheServiceProvider);
        var grantCacheSync = new PermissionProviderGrantCacheSyncService(
            grantRepository,
            grantsCache,
            cacheServiceProvider.GetRequiredService<RolePrivilegeMatrixCache>());
        var sweepService = new PermissionGrantSweepService(
            PermissionsEfCoreTestHarness.AsDbContextProvider(context),
            new ImmediateUnitOfWorkExecutor(),
            stampService,
            grantCacheSync,
            new StaticOptionsMonitor<PermissionGrantSweepOptions>(new PermissionGrantSweepOptions
            {
                Enabled = true,
                IntervalSeconds = 300,
                BatchSize = 500
            }),
            NullLogger<PermissionGrantSweepService>.Instance);

        return await sweepService.SweepAsync();
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

public sealed class PermissionSnapshotCacheExpirationTests
{
    [Fact]
    public void ComputeCacheExpiration_returns_max_when_no_grant_expires()
    {
        var expiration = PermissionSnapshotBuilder.ComputeCacheExpiration(null);

        Assert.Equal(TimeSpan.FromMinutes(20), expiration);
    }

    [Fact]
    public void ComputeCacheExpiration_caps_at_nearest_expiry_when_sooner_than_the_default()
    {
        var nearestExpiry = DateTime.UtcNow.AddMinutes(5);

        var expiration = PermissionSnapshotBuilder.ComputeCacheExpiration(nearestExpiry);

        Assert.True(expiration <= TimeSpan.FromMinutes(5));
        Assert.True(expiration > TimeSpan.FromMinutes(4), $"Expected close to 5 minutes, got {expiration}.");
    }

    [Fact]
    public void ComputeCacheExpiration_falls_back_to_default_when_expiry_is_further_out_than_the_cap()
    {
        var nearestExpiry = DateTime.UtcNow.AddDays(1);

        var expiration = PermissionSnapshotBuilder.ComputeCacheExpiration(nearestExpiry);

        Assert.Equal(TimeSpan.FromMinutes(20), expiration);
    }

    [Fact]
    public void ComputeCacheExpiration_uses_a_defensive_floor_for_an_already_passed_expiry()
    {
        var expiration = PermissionSnapshotBuilder.ComputeCacheExpiration(DateTime.UtcNow.AddSeconds(-1));

        Assert.Equal(TimeSpan.FromSeconds(1), expiration);
    }
}
