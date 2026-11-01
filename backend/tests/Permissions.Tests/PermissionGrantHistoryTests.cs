using Shared.MultiTenancy;
using Shared.Data.Filtering;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionApproval;
using Permissions.Application.Contracts.PermissionHistory;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Definitions;
using Permissions.Application.Evaluation;
using Permissions.Application.Features;
using Permissions.Application.Grants;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Catalog;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Permissions.Tests.TestDoubles;
using Shared.CurrentUser;
using Xunit;

namespace Permissions.Tests;

/// <summary>
/// Coverage for the append-only permission grant-change history (gap claim 8): the recorder, the
/// primary assignment write path (grant/revoke/effect-change), the approval lifecycle, the expiry
/// sweep, and the read query (principal filter, change-type filter, ordering, tenant scope).
/// SQLite (relational) is used because the sweep relies on ExecuteDelete/IgnoreQueryFilters.
/// </summary>
public sealed class PermissionGrantHistoryTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid OtherTenantId = Guid.NewGuid();
    private const string UserKey = "user-1";

    // ---- read query ----

    [Fact]
    public async Task Query_returns_only_the_requested_principal_and_is_tenant_scoped()
    {
        using var connection = OpenDatabase();
        var otherUser = "user-2";

        await SeedHistoryAsync(connection,
            NewEntry(TenantId, PermissionProviderNames.User, UserKey, "Payments.Create", PermissionGrantHistoryChangeType.Granted),
            NewEntry(TenantId, PermissionProviderNames.User, UserKey, "Payments.Create", PermissionGrantHistoryChangeType.Revoked),
            NewEntry(TenantId, PermissionProviderNames.User, otherUser, "Reports.Read", PermissionGrantHistoryChangeType.Granted),
            NewEntry(OtherTenantId, PermissionProviderNames.User, UserKey, "Payments.Create", PermissionGrantHistoryChangeType.Granted));

        var result = await QueryAsync(connection, new GetPermissionGrantHistoryInput
        {
            ProviderName = PermissionProviderNames.User,
            ProviderKey = UserKey
        });

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Items, x => Assert.Equal(UserKey, x.ProviderKey));
        Assert.All(result.Items, x => Assert.Equal(TenantId, x.TenantId));
    }

    [Fact]
    public async Task Query_filters_by_change_type_and_orders_newest_first()
    {
        using var connection = OpenDatabase();
        var baseTime = DateTime.UtcNow;
        var older = new PermissionGrantHistoryEntry(Guid.NewGuid(), TenantId, PermissionProviderNames.User, UserKey, "Payments.Create",
            null, null, null, PermissionGrantHistoryChangeType.Granted, null, PermissionGrantEffect.Allow, null, null, null, Guid.NewGuid(), baseTime.AddMinutes(-1));
        var newer = new PermissionGrantHistoryEntry(Guid.NewGuid(), TenantId, PermissionProviderNames.User, UserKey, "Payments.Create",
            null, null, null, PermissionGrantHistoryChangeType.Revoked, PermissionGrantEffect.Allow, null, null, null, null, Guid.NewGuid(), baseTime);
        await SeedHistoryAsync(connection, older, newer);

        var all = await QueryAsync(connection, new GetPermissionGrantHistoryInput { ProviderKey = UserKey });
        Assert.Equal(PermissionGrantHistoryChangeType.Revoked, all.Items[0].ChangeType);
        Assert.Equal(PermissionGrantHistoryChangeType.Granted, all.Items[1].ChangeType);

        var onlyGrants = await QueryAsync(connection, new GetPermissionGrantHistoryInput
        {
            ProviderKey = UserKey,
            ChangeType = PermissionGrantHistoryChangeType.Granted
        });
        Assert.Equal(1, onlyGrants.TotalCount);
        Assert.Single(onlyGrants.Items);
    }

    [Fact]
    public async Task Platform_query_returns_history_across_tenants_without_changing_tenant_query_scope()
    {
        using var connection = OpenDatabase();
        await SeedHistoryAsync(connection,
            NewEntry(TenantId, PermissionProviderNames.User, UserKey, "Payments.Create", PermissionGrantHistoryChangeType.Granted),
            NewEntry(OtherTenantId, PermissionProviderNames.User, UserKey, "Payments.Create", PermissionGrantHistoryChangeType.Revoked));

        await using var context = Context(connection);
        var service = new PermissionGrantHistoryAppService(
            new EfTestRepository<PermissionGrantHistoryEntry, Guid>(context),
            context.DataFilter,
            SharedOnlyTenantDatabases.FanOut());

        var platformResult = await service.GetPlatformListAsync(
            new GetPermissionGrantHistoryInput { ProviderKey = UserKey });
        var tenantResult = await service.GetListAsync(
            new GetPermissionGrantHistoryInput { ProviderKey = UserKey });

        Assert.Equal(2, platformResult.TotalCount);
        Assert.Contains(platformResult.Items, entry => entry.TenantId == TenantId);
        Assert.Contains(platformResult.Items, entry => entry.TenantId == OtherTenantId);
        Assert.Equal(1, tenantResult.TotalCount);
        Assert.All(tenantResult.Items, entry => Assert.Equal(TenantId, entry.TenantId));
    }

    // ---- recorder ----

    [Fact]
    public async Task Recorder_appends_an_entry_with_actor_and_effects()
    {
        using var connection = OpenDatabase();
        var actor = Guid.NewGuid();

        await using (var context = Context(connection))
        {
            var recorder = new PermissionGrantHistoryRecorder(
                new EfTestRepository<PermissionGrantHistoryEntry, Guid>(context),
                new StubCurrentUser(actor));
            await recorder.RecordAsync(
                TenantId, PermissionProviderNames.User, UserKey, "Payments.Create",
                PermissionGrantHistoryChangeType.EffectChanged,
                oldEffect: PermissionGrantEffect.Allow, newEffect: PermissionGrantEffect.Deny,
                expiresAtUtc: null, reason: "risk", ticketId: "T-9");
            await context.SaveChangesAsync();
        }

        var entry = Assert.Single(await ReadAllHistoryAsync(connection));
        Assert.Equal(actor, entry.ActorUserId);
        Assert.Equal(PermissionGrantEffect.Allow, entry.OldEffect);
        Assert.Equal(PermissionGrantEffect.Deny, entry.NewEffect);
        Assert.Equal("T-9", entry.TicketId);
    }

    // ---- assignment write path ----

    [Fact]
    public async Task Assignment_records_granted_then_effect_change_then_revoke()
    {
        using var connection = OpenDatabase();

        await UpdateAsync(connection, granted: true, PermissionGrantEffect.Allow);
        await UpdateAsync(connection, granted: true, PermissionGrantEffect.Deny);
        await UpdateAsync(connection, granted: false, PermissionGrantEffect.Allow);

        var history = (await ReadAllHistoryAsync(connection))
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => x.ChangeType)
            .ToList();

        Assert.Contains(history, x => x.ChangeType == PermissionGrantHistoryChangeType.Granted && x.NewEffect == PermissionGrantEffect.Allow);
        Assert.Contains(history, x => x.ChangeType == PermissionGrantHistoryChangeType.EffectChanged && x.OldEffect == PermissionGrantEffect.Allow && x.NewEffect == PermissionGrantEffect.Deny);
        Assert.Contains(history, x => x.ChangeType == PermissionGrantHistoryChangeType.Revoked && x.OldEffect == PermissionGrantEffect.Deny);
    }

    // ---- expiry sweep ----

    [Fact]
    public async Task Sweep_records_expired_history_for_a_swept_grant()
    {
        using var connection = OpenDatabase();
        await SeedGrantAsync(connection, "Payments.Create", DateTime.UtcNow.AddMinutes(-5));

        await RunSweepAsync(connection);

        var entry = Assert.Single(await ReadAllHistoryAsync(connection));
        Assert.Equal(PermissionGrantHistoryChangeType.Expired, entry.ChangeType);
        Assert.Equal("Payments.Create", entry.PermissionName);
        Assert.Null(entry.ActorUserId);
        Assert.Equal(PermissionGrantEffect.Allow, entry.OldEffect);
    }

    // ---- approval lifecycle ----

    [Fact]
    public async Task Approval_request_then_approve_records_requested_then_granted()
    {
        using var connection = OpenDatabase();
        var requester = Guid.NewGuid();

        await using (var context = Context(connection))
        {
            var (svc, _) = BuildApproval(context, requester);
            await svc.CreatePendingAsync(TenantId, PermissionProviderNames.User, UserKey, "Payments.Create", null, "need", "T-1", requester);
            await context.SaveChangesAsync();
        }

        var requestId = (await ReadPendingRequestIdAsync(connection));

        await using (var context = Context(connection))
        {
            var (svc, _) = BuildApproval(context, Guid.NewGuid());
            await svc.ApproveAsync(requestId, new DecideGrantChangeRequestInput { DecisionReason = "ok", ExpectedVersion = 1, IdempotencyKey = "approve-history-1" });
        }

        var history = await ReadAllHistoryAsync(connection);
        Assert.Contains(history, x => x.ChangeType == PermissionGrantHistoryChangeType.ApprovalRequested);
        Assert.Contains(history, x => x.ChangeType == PermissionGrantHistoryChangeType.Granted);
    }

    [Fact]
    public async Task Approval_reject_records_approval_rejected()
    {
        using var connection = OpenDatabase();
        var requester = Guid.NewGuid();

        await using (var context = Context(connection))
        {
            var (svc, _) = BuildApproval(context, requester);
            await svc.CreatePendingAsync(TenantId, PermissionProviderNames.User, UserKey, "Payments.Create", null, null, null, requester);
            await context.SaveChangesAsync();
        }

        var requestId = await ReadPendingRequestIdAsync(connection);

        await using (var context = Context(connection))
        {
            var (svc, _) = BuildApproval(context, Guid.NewGuid());
            await svc.RejectAsync(requestId, new DecideGrantChangeRequestInput { DecisionReason = "no", ExpectedVersion = 1, IdempotencyKey = "reject-history-1" });
        }

        var history = await ReadAllHistoryAsync(connection);
        Assert.Contains(history, x => x.ChangeType == PermissionGrantHistoryChangeType.ApprovalRejected);
    }

    // ---- helpers ----

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        connection.Open();
        using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null, multiTenantFilterEnabled: true);
        context.Database.EnsureCreated();
        return connection;
    }

    private static PermissionsDbContextApp Context(SqliteConnection connection, Guid? tenantId)
        => PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId, multiTenantFilterEnabled: true);

    private static PermissionsDbContextApp Context(SqliteConnection connection)
        => Context(connection, TenantId);

    private static PermissionGrantHistoryEntry NewEntry(
        Guid? tenantId, string providerName, string providerKey, string permission, PermissionGrantHistoryChangeType changeType)
        => new(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            providerName: providerName,
            providerKey: providerKey,
            permissionName: permission,
            resourceName: null,
            resourceKey: null,
            resourceOwnerRevision: null,
            changeType: changeType,
            oldEffect: null,
            newEffect: changeType == PermissionGrantHistoryChangeType.Granted ? PermissionGrantEffect.Allow : null,
            expiresAtUtc: null,
            reason: null,
            ticketId: null,
            actorUserId: Guid.NewGuid(),
            occurredAtUtc: DateTime.UtcNow.AddMilliseconds(Random.Shared.Next(1, 100000)));

    private static async Task SeedHistoryAsync(SqliteConnection connection, params PermissionGrantHistoryEntry[] entries)
    {
        // No tenant filter so rows for multiple tenants persist.
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null, multiTenantFilterEnabled: false);
        context.PermissionGrantHistoryEntries.AddRange(entries);
        await context.SaveChangesAsync();
    }

    private static async Task<PermissionGrantHistoryResultDto> QueryAsync(SqliteConnection connection, GetPermissionGrantHistoryInput input)
    {
        await using var context = Context(connection);
        var service = new PermissionGrantHistoryAppService(
            new EfTestRepository<PermissionGrantHistoryEntry, Guid>(context),
            context.DataFilter,
            SharedOnlyTenantDatabases.FanOut());
        return await service.GetListAsync(input);
    }

    private static async Task<List<PermissionGrantHistoryEntry>> ReadAllHistoryAsync(SqliteConnection connection)
    {
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null, multiTenantFilterEnabled: false);
        return await context.PermissionGrantHistoryEntries.IgnoreQueryFilters().ToListAsync();
    }

    private static async Task<Guid> ReadPendingRequestIdAsync(SqliteConnection connection)
    {
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null, multiTenantFilterEnabled: false);
        var pending = await context.PermissionGrantChangeRequests
            .IgnoreQueryFilters()
            .FirstAsync(x => x.Status == PermissionGrantChangeRequestStatus.Pending);
        return pending.Id;
    }

    private static async Task SeedGrantAsync(SqliteConnection connection, string permission, DateTime expiresAtUtc)
    {
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null, multiTenantFilterEnabled: true);
        context.PermissionGrants.Add(new PermissionGrant(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            name: permission,
            providerName: PermissionProviderNames.User,
            providerKey: UserKey,
            effect: PermissionGrantEffect.Allow,
            creationTimeUtc: DateTime.UtcNow,
            expiresAtUtc: expiresAtUtc));
        await context.SaveChangesAsync();
    }

    private static async Task RunSweepAsync(SqliteConnection connection)
    {
        var cacheServiceProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: null, multiTenantFilterEnabled: true);
        var grantRepository = new EfTestRepository<PermissionGrant, Guid>(context);
        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheServiceProvider);
        var grantsCache = ActivatorUtilities.CreateInstance<PermissionProviderGrantsCache>(cacheServiceProvider);
        var grantCacheSync = new PermissionProviderGrantCacheSyncService(
            grantRepository,
            grantsCache,
            cacheServiceProvider.GetRequiredService<RolePrivilegeMatrixCache>());
        var sweep = new PermissionGrantSweepService(
            PermissionsEfCoreTestHarness.AsDbContextProvider(context),
            new ImmediateUnitOfWorkExecutor(),
            stampService,
            grantCacheSync,
            new StaticOptionsMonitor<PermissionGrantSweepOptions>(new PermissionGrantSweepOptions { Enabled = true, BatchSize = 500 }),
            NullLogger<PermissionGrantSweepService>.Instance);
        await sweep.SweepAsync();
    }

    private static async Task UpdateAsync(SqliteConnection connection, bool granted, PermissionGrantEffect effect)
    {
        await using var context = Context(connection);
        var service = BuildAssignment(context, out _);
        await service.UpdateAsync(
            PermissionProviderNames.User,
            UserKey,
            new UpdatePermissionsDto
            {
                ExpectedProviderVersion = await GetProviderVersionAsync(context),
                Permissions = [new UpdatePermissionDto { Name = "Payments.Create", IsGranted = granted, Effect = effect }]
            });
        await context.SaveChangesAsync();
    }

    private static async Task<string> GetProviderVersionAsync(PermissionsDbContextApp context)
    {
        var version = await context.PermissionAssignmentStates
            .AsNoTracking()
            .Where(state => state.TenantId == TenantId &&
                            state.ProviderName == PermissionProviderNames.User &&
                            state.ProviderKey == UserKey)
            .Select(state => (Guid?)state.Version)
            .SingleOrDefaultAsync();
        return version?.ToString("D") ?? PermissionAssignmentAppService.InitialProviderVersion;
    }

    private static PermissionAssignmentAppService BuildAssignment(PermissionsDbContextApp context, out PermissionGrantHistoryRecorder recorder)
    {
        var cacheServiceProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheServiceProvider);
        var grantsCache = ActivatorUtilities.CreateInstance<PermissionProviderGrantsCache>(cacheServiceProvider);
        var grantRepository = new EfTestRepository<PermissionGrant, Guid>(context);
        var catalogRepository = new EfTestRepository<PermissionNode, Guid>(context);
        var conflictRuleRepository = new EfTestRepository<Permissions.Domain.Grants.PermissionConflictRule, Guid>(context);
        var grantCacheSync = new PermissionProviderGrantCacheSyncService(
            grantRepository,
            grantsCache,
            cacheServiceProvider.GetRequiredService<RolePrivilegeMatrixCache>());
        var conflictRules = new PermissionConflictRuleService(conflictRuleRepository);
        var definitionManager = new PermissionDefinitionManager([new HistoryTestDefinitionProvider()]);
        var definitionIndex = new PermissionDefinitionIndex(definitionManager);
        var snapshotCache = ActivatorUtilities.CreateInstance<PermissionSnapshotCache>(cacheServiceProvider);
        var snapshotBuilder = new PermissionSnapshotBuilder(
            grantRepository,
            new PermissionProviderCacheRepairService(grantRepository, grantCacheSync, stampService, context.ServiceProvider.GetRequiredService<IDataFilter>(), context.ServiceProvider.GetRequiredService<ICurrentTenant>(), NullLogger<PermissionProviderCacheRepairService>.Instance),
            snapshotCache,
            NullLogger<PermissionSnapshotBuilder>.Instance);
        var currentUser = new StubCurrentUser(Guid.NewGuid());
        recorder = new PermissionGrantHistoryRecorder(new EfTestRepository<PermissionGrantHistoryEntry, Guid>(context), currentUser);

        var approval = new PermissionApprovalAppService(
            new EfTestRepository<PermissionGrantChangeRequest, Guid>(context),
            grantRepository,
            new EfTestRepository<PermissionApprovalDecisionOperation, Guid>(context),
            new TestCurrentTenant(TenantId),
            currentUser,
            stampService,
            grantCacheSync,
            recorder,
            new AllowPendingPermissionGrantValidator(),
            definitionManager,
            new NoopPermissionApprovalDisplayProjectionResolver(),
            new NoopPermissionApprovalNotificationPublisher());

        return new PermissionAssignmentAppService(
            new AllowAllAuthorizationService(),
            new TestCurrentTenant(TenantId),
            currentUser,
            definitionManager,
            definitionIndex,
            snapshotBuilder,
            new PermissionDecisionEngine(),
            new PermissionFeatureChecker(new EfTestRepository<Permissions.Domain.Features.PermissionFeatureBinding, Guid>(context), new AllowAllFeatureCheckerClient()),
            new AllowAllFeatureCheckerClient(),
            catalogRepository,
            grantRepository,
            grantCacheSync,
            stampService,
            conflictRules,
            new StaticIdentityPermissionTargetValidator(),
            approval,
            recorder,
            PermissionsEfCoreTestHarness.AsDbContextProvider(context));
    }

    private static (PermissionApprovalAppService Service, PermissionGrantHistoryRecorder Recorder) BuildApproval(
        PermissionsDbContextApp context, Guid? currentUserId)
    {
        var cacheServiceProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheServiceProvider);
        var grantsCache = ActivatorUtilities.CreateInstance<PermissionProviderGrantsCache>(cacheServiceProvider);
        var grantRepository = new EfTestRepository<PermissionGrant, Guid>(context);
        var grantCacheSync = new PermissionProviderGrantCacheSyncService(
            grantRepository,
            grantsCache,
            cacheServiceProvider.GetRequiredService<RolePrivilegeMatrixCache>());
        var currentUser = new StubCurrentUser(currentUserId);
        var recorder = new PermissionGrantHistoryRecorder(new EfTestRepository<PermissionGrantHistoryEntry, Guid>(context), currentUser);
        var service = new PermissionApprovalAppService(
            new EfTestRepository<PermissionGrantChangeRequest, Guid>(context),
            grantRepository,
            new EfTestRepository<PermissionApprovalDecisionOperation, Guid>(context),
            new TestCurrentTenant(TenantId),
            currentUser,
            stampService,
            grantCacheSync,
            recorder,
            new AllowPendingPermissionGrantValidator(),
            new PermissionDefinitionManager([new HistoryTestDefinitionProvider()]),
            new NoopPermissionApprovalDisplayProjectionResolver(),
            new NoopPermissionApprovalNotificationPublisher());
        return (service, recorder);
    }

    private sealed class HistoryTestDefinitionProvider : PermissionDefinitionProvider
    {
        public override void Define(IPermissionDefinitionContext context)
        {
            var group = context.AddGroup("Payments", "Payments");
            group.AddPermission("Payments.Create", "Create");
        }
    }

    private sealed class AllowAllAuthorizationService : IRequestAuthorizationService
    {
        public Task EnsureAuthorizedAsync(string policyName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowAllFeatureCheckerClient : IFeatureCheckerClient
    {
        public Task<bool> IsEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<Dictionary<string, bool>> AreEnabledAsync(Guid? tenantId, IReadOnlyCollection<string> featureNames, CancellationToken cancellationToken = default)
            => Task.FromResult(featureNames.ToDictionary(x => x, _ => true, StringComparer.Ordinal));
    }

    private sealed class StaticIdentityPermissionTargetValidator : IIdentityPermissionTargetValidator
    {
        public Task<IdentityRolePermissionTarget> GetRoleAsync(Guid roleId, Guid? tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IdentityRolePermissionTarget(
                roleId, "PlatformRole", IsStatic: false, IsActive: true, 0));

        public Task<IdentityUserPermissionTarget> GetUserAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IdentityUserPermissionTarget(userId, "user", "User", true, false, []));

        public Task<IReadOnlyList<IdentityResolvedPermissionTarget>> ResolveAsync(IReadOnlyCollection<IdentityPermissionTargetReference> targets, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IdentityResolvedPermissionTarget>>([]);
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public StubCurrentUser(Guid? id) => Id = id;

        public bool IsAuthenticated => Id.HasValue;
        public Guid? Id { get; }
        public Guid? TenantId => PermissionGrantHistoryTests.TenantId;
        public string? SessionId => null;
        public string? UserName => null;
        public string? Name => null;
        public string? SurName => null;
        public string? Email => null;
        public bool EmailVerified => false;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string[] Roles => [];
        public System.Security.Claims.Claim? FindClaim(string claimType) => null;
        public System.Security.Claims.Claim[] FindClaims(string claimType) => [];
        public System.Security.Claims.Claim[] GetAllClaims() => [];
        public bool IsInRole(string roleName) => false;
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
