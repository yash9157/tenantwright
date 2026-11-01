using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Permissions.Application.AccessReviews;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.AccessReviews;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Definitions;
using Permissions.Application.Evaluation;
using Permissions.Application.Features;
using Permissions.Application.Grants;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Catalog;
using Permissions.Domain.Features;
using Permissions.Domain.Grants;
using Permissions.Domain.PermissionManagement;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Permissions.Tests.TestDoubles;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.MultiTenancy;
using Shared.Security;
using Xunit;

namespace Permissions.Tests;

/// <summary>
/// Relational coverage for tenant-owned access-review snapshots and decisions. The service is
/// deliberately built in host context because a host operator may select exactly one tenant.
/// </summary>
public sealed class AccessReviewAppServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string ProviderKey = "review-target";
    private const string PermissionName = "Documents.Read";

    [Fact]
    public async Task Campaign_snapshots_only_active_allow_grants_and_keeps_the_snapshot_immutable()
    {
        using var connection = OpenDatabase();
        await SeedAsync(connection,
            NewGrant(TenantId, PermissionGrantEffect.Allow, expiresAtUtc: null),
            NewGrant(TenantId, PermissionGrantEffect.Deny, expiresAtUtc: null, providerKey: "denied-target"),
            NewGrant(TenantId, PermissionGrantEffect.Allow, DateTime.UtcNow.AddMinutes(-1), providerKey: "expired-target"));

        var (service, _) = BuildService(connection);
        var campaign = await service.CreateCampaignAsync(new CreateAccessReviewCampaignInput
        {
            TenantId = TenantId,
            Name = "Quarterly access review",
            DueAtUtc = DateTime.UtcNow.AddDays(14)
        });

        await using var verification = CreateContext(connection, tenantId: null, multiTenantFilterEnabled: false);
        var item = Assert.Single(await verification.AccessReviewItems
            .IgnoreQueryFilters()
            .Where(x => x.CampaignId == campaign.Id)
            .ToListAsync());

        Assert.Equal(PermissionGrantEffect.Allow, item.Effect);
        Assert.Equal(PermissionName, item.PermissionName);
        Assert.Equal(TenantId, item.TenantId);
        Assert.Equal(1, item.Version);
    }

    [Fact]
    public async Task Host_selected_tenant_revoke_removes_only_the_reviewed_tenant_grant_and_records_normal_mutation_effects()
    {
        using var connection = OpenDatabase();
        var hostGrant = NewGrant(tenantId: null, PermissionGrantEffect.Allow, expiresAtUtc: null);
        var tenantGrant = NewGrant(TenantId, PermissionGrantEffect.Allow, expiresAtUtc: null);
        await SeedAsync(connection, hostGrant, tenantGrant);

        var (service, cacheProvider) = BuildService(connection);
        var campaign = await service.CreateCampaignAsync(new CreateAccessReviewCampaignInput
        {
            TenantId = TenantId,
            Name = "Quarterly access review",
            DueAtUtc = DateTime.UtcNow.AddDays(14)
        });
        var item = Assert.Single((await service.GetItemsAsync(campaign.Id, new GetAccessReviewItemsInput
        {
            TenantId = TenantId
        })).Items);

        var result = await service.DecideItemAsync(campaign.Id, item.Id, new DecideAccessReviewItemInput
        {
            TenantId = TenantId,
            Decision = AccessReviewDecision.Revoke,
            DecisionReason = "No longer required",
            ExpectedVersion = item.Version
        });

        Assert.Equal(AccessReviewItemStatus.Revoked, result.Status);
        Assert.Equal(2, result.Version);

        await using var verification = CreateContext(connection, tenantId: null, multiTenantFilterEnabled: false);
        Assert.Null(await verification.PermissionGrants.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == tenantGrant.Id));
        Assert.NotNull(await verification.PermissionGrants.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == hostGrant.Id));

        var history = Assert.Single(await verification.PermissionGrantHistoryEntries
            .IgnoreQueryFilters()
            .Where(x => x.TenantId == TenantId && x.PermissionName == PermissionName)
            .ToListAsync());
        Assert.Equal(PermissionGrantHistoryChangeType.Revoked, history.ChangeType);
        Assert.Equal(PermissionGrantEffect.Allow, history.OldEffect);

        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheProvider);
        Assert.True(await stampService.GetAsync(TenantId, PermissionProviderNames.User, ProviderKey) > 0);
        Assert.Equal(0, await stampService.GetAsync(null, PermissionProviderNames.User, ProviderKey));
    }

    [Fact]
    public async Task A_stale_item_version_returns_a_structured_refresh_conflict()
    {
        using var connection = OpenDatabase();
        await SeedAsync(connection, NewGrant(TenantId, PermissionGrantEffect.Allow, expiresAtUtc: null));

        var (service, _) = BuildService(connection);
        var campaign = await service.CreateCampaignAsync(new CreateAccessReviewCampaignInput
        {
            TenantId = TenantId,
            Name = "Quarterly access review",
            DueAtUtc = DateTime.UtcNow.AddDays(14)
        });
        var item = Assert.Single((await service.GetItemsAsync(campaign.Id, new GetAccessReviewItemsInput
        {
            TenantId = TenantId
        })).Items);

        var exception = await Assert.ThrowsAsync<AccessReviewConflictException>(() => service.DecideItemAsync(
            campaign.Id,
            item.Id,
            new DecideAccessReviewItemInput
            {
                TenantId = TenantId,
                Decision = AccessReviewDecision.Certify,
                DecisionReason = "Still required",
                ExpectedVersion = item.Version + 1
            }));

        Assert.Equal(AccessReviewConflictCode.VersionConflict, exception.Conflict.Code);
        Assert.Equal(item.Version, exception.Conflict.CurrentVersion);
        Assert.Equal(AccessReviewItemStatus.Pending, exception.Conflict.CurrentStatus);
    }

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        using var context = CreateContext(connection, tenantId: null, multiTenantFilterEnabled: false);
        context.Database.EnsureCreated();
        return connection;
    }

    private static PermissionsDbContextApp CreateContext(
        SqliteConnection connection,
        Guid? tenantId,
        bool multiTenantFilterEnabled)
        => PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId, multiTenantFilterEnabled);

    private static async Task SeedAsync(SqliteConnection connection, params PermissionGrant[] grants)
    {
        await using var context = CreateContext(connection, tenantId: null, multiTenantFilterEnabled: false);
        context.PermissionGrants.AddRange(grants);
        await context.SaveChangesAsync();
    }

    private static PermissionGrant NewGrant(
        Guid? tenantId,
        PermissionGrantEffect effect,
        DateTime? expiresAtUtc,
        string? providerKey = null)
        => new(
            Guid.NewGuid(),
            tenantId,
            PermissionName,
            PermissionProviderNames.User,
            providerKey ?? ProviderKey,
            effect,
            DateTime.UtcNow,
            expiresAtUtc: expiresAtUtc,
            reason: "Access review fixture",
            ticketId: "AR-1");

    private static (AccessReviewAppService Service, IServiceProvider CacheProvider) BuildService(SqliteConnection connection)
    {
        var context = CreateContext(connection, tenantId: null, multiTenantFilterEnabled: false);
        var cacheProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var currentTenant = new TestCurrentTenant(null);
        var currentUser = new ReviewCurrentUser(Guid.NewGuid());
        var grantRepository = new EfTestRepository<PermissionGrant, Guid>(context);
        var definitions = new PermissionDefinitionManager([new ReviewDefinitionProvider()]);
        var grantCacheSync = new PermissionProviderGrantCacheSyncService(
            grantRepository,
            cacheProvider.GetRequiredService<PermissionProviderGrantsCache>(),
            cacheProvider.GetRequiredService<RolePrivilegeMatrixCache>());
        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheProvider);
        var history = new PermissionGrantHistoryRecorder(
            new EfTestRepository<PermissionGrantHistoryEntry, Guid>(context),
            currentUser);
        var approval = new PermissionApprovalAppService(
            new EfTestRepository<PermissionGrantChangeRequest, Guid>(context),
            grantRepository,
            new EfTestRepository<PermissionApprovalDecisionOperation, Guid>(context),
            currentTenant,
            currentUser,
            stampService,
            grantCacheSync,
            history,
            new AllowPendingPermissionGrantValidator(),
            definitions,
            new NoopPermissionApprovalDisplayProjectionResolver(),
            new NoopPermissionApprovalNotificationPublisher());
        var snapshot = new PermissionSnapshotBuilder(
            grantRepository,
            new PermissionProviderCacheRepairService(
                grantRepository,
                grantCacheSync,
                stampService,
                context.ServiceProvider.GetRequiredService<IDataFilter>(),
                context.ServiceProvider.GetRequiredService<ICurrentTenant>(),
                NullLogger<PermissionProviderCacheRepairService>.Instance),
            cacheProvider.GetRequiredService<PermissionSnapshotCache>(),
            NullLogger<PermissionSnapshotBuilder>.Instance);
        var assignment = new PermissionAssignmentAppService(
            new AllowAllAuthorizationService(),
            currentTenant,
            currentUser,
            definitions,
            new PermissionDefinitionIndex(definitions),
            snapshot,
            new PermissionDecisionEngine(),
            new PermissionFeatureChecker(
                new EfTestRepository<PermissionFeatureBinding, Guid>(context),
                new AllowAllFeatureCheckerClient()),
            new AllowAllFeatureCheckerClient(),
            new EfTestRepository<PermissionNode, Guid>(context),
            grantRepository,
            grantCacheSync,
            stampService,
            new PermissionConflictRuleService(new EfTestRepository<PermissionConflictRule, Guid>(context)),
            new ReviewIdentityTargets(),
            approval,
            history,
            PermissionsEfCoreTestHarness.AsDbContextProvider(context));
        var service = new AccessReviewAppService(
            PermissionsEfCoreTestHarness.AsDbContextProvider(context),
            new AllowAllAuthorizationService(),
            currentTenant,
            currentUser,
            new ConfigurableDataFilter(enabled: false),
            assignment,
            definitions,
            new ReviewIdentityTargets(),
            cacheProvider.GetRequiredService<RolePrivilegeMatrixCache>(),
            SharedOnlyTenantDatabases.FanOut(currentTenant));

        return (service, cacheProvider);
    }

    private sealed class ReviewDefinitionProvider : PermissionDefinitionProvider
    {
        public override void Define(IPermissionDefinitionContext context)
        {
            var group = context.AddGroup("Documents", "Documents");
            group.AddPermission(PermissionName, "Read documents");
        }
    }

    private sealed class AllowAllAuthorizationService : IRequestAuthorizationService
    {
        public Task EnsureAuthorizedAsync(string policyName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowAllFeatureCheckerClient : IFeatureCheckerClient
    {
        public Task<bool> IsEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<Dictionary<string, bool>> AreEnabledAsync(
            Guid? tenantId,
            IReadOnlyCollection<string> featureNames,
            CancellationToken cancellationToken = default)
            => Task.FromResult(featureNames.ToDictionary(x => x, _ => true, StringComparer.Ordinal));
    }

    private sealed class ReviewIdentityTargets : IIdentityPermissionTargetValidator
    {
        public Task<IdentityRolePermissionTarget> GetRoleAsync(Guid roleId, Guid? tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IdentityRolePermissionTarget(roleId, "Role", IsStatic: false, IsActive: true, 0));

        public Task<IdentityUserPermissionTarget> GetUserAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IdentityUserPermissionTarget(userId, "User", "Review user", true, false, []));

        public Task<IReadOnlyList<IdentityResolvedPermissionTarget>> ResolveAsync(
            IReadOnlyCollection<IdentityPermissionTargetReference> targets,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IdentityResolvedPermissionTarget>>(
                targets.Select(target => new IdentityResolvedPermissionTarget(
                    target.Kind,
                    target.Id,
                    target.TenantId,
                    target.Kind == IdentityPermissionTargetKind.Role ? "Role" : "Review user"))
                    .ToList());
    }

    private sealed class ReviewCurrentUser : ICurrentUser
    {
        public ReviewCurrentUser(Guid id) => Id = id;

        public bool IsAuthenticated => true;
        public Guid? Id { get; }
        public Guid? TenantId => null;
        public string? SessionId => null;
        public string? UserName => "reviewer";
        public string? Name => "Review";
        public string? SurName => "Operator";
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
}
