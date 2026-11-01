using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.PlatformAssignments;
using Permissions.Application.Definitions;
using Permissions.Application.Evaluation;
using Permissions.Application.Features;
using Permissions.Application.Grants;
using Permissions.Application.PermissionManagement;
using Permissions.Application.PlatformAssignments;
using Permissions.Domain.Catalog;
using Permissions.Domain.Features;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.Tests.TestDoubles;
using Shared.CurrentUser;
using Shared.MultiTenancy;
using System.Linq;
using Xunit;

namespace Permissions.Tests;

/// <summary>
/// Integration coverage for sub-flow 5's primary control: <see cref="PermissionAssignmentAppService"/>
/// rejects granting a permission that separation-of-duty rules mark as conflicting with a permission
/// the target already effectively holds.
/// </summary>
public sealed class PermissionConflictRuleEnforcementTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string ProviderKey = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public async Task Granting_a_conflicting_permission_is_rejected_when_the_target_already_holds_the_other_side()
    {
        using var connection = OpenDatabase();
        await SeedConflictRuleAsync(connection, "Payments.Approve", "Payments.Create");
        await SeedGrantAsync(connection, "Payments.Approve");

        var service = BuildService(connection);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(PermissionProviderNames.Role, ProviderKey, new UpdatePermissionsDto
            {
                ExpectedProviderVersion = PermissionAssignmentAppService.InitialProviderVersion,
                Permissions = [new() { Name = "Payments.Create", IsGranted = true, Effect = PermissionGrantEffect.Allow }]
            }));

        Assert.Contains("Payments.Create", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Payments.Approve", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Granting_an_unrelated_permission_succeeds_even_with_a_conflict_rule_defined()
    {
        using var connection = OpenDatabase();
        await SeedConflictRuleAsync(connection, "Payments.Approve", "Payments.Create");
        await SeedGrantAsync(connection, "Payments.Approve");

        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true);
        var service = BuildServiceForContext(context);

        await service.UpdateAsync(PermissionProviderNames.Role, ProviderKey, new UpdatePermissionsDto
        {
            ExpectedProviderVersion = PermissionAssignmentAppService.InitialProviderVersion,
            Permissions = [new() { Name = "Reports.Read", IsGranted = true, Effect = PermissionGrantEffect.Allow }]
        });
        await context.SaveChangesAsync();

        var grantCount = await CountGrantsAsync(connection, "Reports.Read");
        Assert.Equal(1, grantCount);
    }

    [Fact]
    public async Task Denying_a_conflicting_permission_is_not_rejected()
    {
        // A Deny grant does not create a conflict (nothing is "held" as Allow), so it must go through.
        using var connection = OpenDatabase();
        await SeedConflictRuleAsync(connection, "Payments.Approve", "Payments.Create");
        await SeedGrantAsync(connection, "Payments.Approve");

        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true);
        var service = BuildServiceForContext(context);

        await service.UpdateAsync(PermissionProviderNames.Role, ProviderKey, new UpdatePermissionsDto
        {
            ExpectedProviderVersion = PermissionAssignmentAppService.InitialProviderVersion,
            Permissions = [new() { Name = "Payments.Create", IsGranted = true, Effect = PermissionGrantEffect.Deny }]
        });
        await context.SaveChangesAsync();

        var grantCount = await CountGrantsAsync(connection, "Payments.Create");
        Assert.Equal(1, grantCount);
    }

    [Fact]
    public async Task Granting_both_conflicting_sides_in_the_same_batch_is_rejected()
    {
        using var connection = OpenDatabase();
        await SeedConflictRuleAsync(connection, "Payments.Approve", "Payments.Create");

        var service = BuildService(connection);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(PermissionProviderNames.Role, ProviderKey, new UpdatePermissionsDto
            {
                ExpectedProviderVersion = PermissionAssignmentAppService.InitialProviderVersion,
                Permissions =
                [
                    new() { Name = "Payments.Approve", IsGranted = true, Effect = PermissionGrantEffect.Allow },
                    new() { Name = "Payments.Create", IsGranted = true, Effect = PermissionGrantEffect.Allow }
                ]
            }));

        Assert.Contains("conflicts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stale_provider_version_is_rejected_before_a_second_assignment_write()
    {
        using var connection = OpenDatabase();
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(
            connection,
            tenantId: TenantId,
            multiTenantFilterEnabled: true);
        var service = BuildServiceForContext(context);

        await service.UpdateAsync(PermissionProviderNames.Role, ProviderKey, new UpdatePermissionsDto
        {
            ExpectedProviderVersion = PermissionAssignmentAppService.InitialProviderVersion,
            Permissions = [new() { Name = "Reports.Read", IsGranted = true }]
        });

        var exception = await Assert.ThrowsAsync<PlatformPermissionAssignmentConflictException>(() =>
            service.UpdateAsync(PermissionProviderNames.Role, ProviderKey, new UpdatePermissionsDto
            {
                ExpectedProviderVersion = PermissionAssignmentAppService.InitialProviderVersion,
                Permissions = [new() { Name = "Payments.Create", IsGranted = true }]
            }));

        Assert.Equal(PermissionAssignmentConflictCode.ProviderVersionConflict, exception.Conflict.Code);
        Assert.NotEqual(PermissionAssignmentAppService.InitialProviderVersion, exception.Conflict.CurrentProviderVersion);
    }

    private static async Task SeedConflictRuleAsync(SqliteConnection connection, string permissionA, string permissionB)
    {
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true);
        context.PermissionConflictRules.Add(new PermissionConflictRule(Guid.NewGuid(), TenantId, permissionA, permissionB, DateTime.UtcNow));
        await context.SaveChangesAsync();
    }

    private static async Task SeedGrantAsync(SqliteConnection connection, string permissionName)
    {
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true);
        context.PermissionGrants.Add(new PermissionGrant(
            Guid.NewGuid(),
            TenantId,
            permissionName,
            PermissionProviderNames.Role,
            ProviderKey,
            PermissionGrantEffect.Allow,
            DateTime.UtcNow));
        await context.SaveChangesAsync();
    }

    private static async Task<int> CountGrantsAsync(SqliteConnection connection, string permissionName)
    {
        await using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true);
        return await context.PermissionGrants.CountAsync(x => x.Name == permissionName);
    }

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        connection.Open();
        using var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true);
        context.Database.EnsureCreated();
        return connection;
    }

    private static PermissionAssignmentAppService BuildService(SqliteConnection connection)
    {
        var context = PermissionsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId: TenantId, multiTenantFilterEnabled: true);
        return BuildServiceForContext(context);
    }

    private static PermissionAssignmentAppService BuildServiceForContext(Permissions.EntityFrameworkCore.EntityFrameworkCore.PermissionsDbContextApp context)
    {
        var cacheServiceProvider = PermissionsCachingTestHarness.BuildServiceProvider();
        var stampService = ActivatorUtilities.CreateInstance<PermissionStampService>(cacheServiceProvider);
        var grantsCache = ActivatorUtilities.CreateInstance<PermissionProviderGrantsCache>(cacheServiceProvider);

        var grantRepository = new EfTestRepository<PermissionGrant, Guid>(context);
        var catalogRepository = new EfTestRepository<PermissionNode, Guid>(context);
        var conflictRuleRepository = new EfTestRepository<PermissionConflictRule, Guid>(context);

        var grantCacheSync = new PermissionProviderGrantCacheSyncService(
            grantRepository,
            grantsCache,
            cacheServiceProvider.GetRequiredService<RolePrivilegeMatrixCache>());
        var conflictRules = new PermissionConflictRuleService(conflictRuleRepository);

        var definitionManager = new PermissionDefinitionManager([new TestPermissionDefinitionProvider()]);
        var definitionIndex = new PermissionDefinitionIndex(definitionManager);

        var snapshotCache = ActivatorUtilities.CreateInstance<PermissionSnapshotCache>(cacheServiceProvider);
        var snapshotBuilder = new PermissionSnapshotBuilder(
            grantRepository,
            new Permissions.Application.Caching.PermissionProviderCacheRepairService(grantRepository, grantCacheSync, stampService, context.ServiceProvider.GetRequiredService<Shared.Data.Filtering.IDataFilter>(), context.ServiceProvider.GetRequiredService<ICurrentTenant>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Permissions.Application.Caching.PermissionProviderCacheRepairService>.Instance),
            snapshotCache,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PermissionSnapshotBuilder>.Instance);

        var currentUser = new NullCurrentUser();
        var historyRecorder = new Permissions.Application.Grants.PermissionGrantHistoryRecorder(
            new EfTestRepository<PermissionGrantHistoryEntry, Guid>(context),
            currentUser);

        var approvalService = new PermissionApprovalAppService(
            new EfTestRepository<PermissionGrantChangeRequest, Guid>(context),
            grantRepository,
            new EfTestRepository<PermissionApprovalDecisionOperation, Guid>(context),
            new TestCurrentTenant(TenantId),
            currentUser,
            stampService,
            grantCacheSync,
            historyRecorder,
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
            new PermissionFeatureChecker(new EfTestRepository<PermissionFeatureBinding, Guid>(context), new AllowAllFeatureCheckerClient()),
            new AllowAllFeatureCheckerClient(),
            catalogRepository,
            grantRepository,
            grantCacheSync,
            stampService,
            conflictRules,
            new StaticIdentityPermissionTargetValidator(),
            approvalService,
            historyRecorder,
            PermissionsEfCoreTestHarness.AsDbContextProvider(context));
    }

    private sealed class TestPermissionDefinitionProvider : PermissionDefinitionProvider
    {
        public override void Define(IPermissionDefinitionContext context)
        {
            var group = context.AddGroup("Payments", "Payments");
            group.AddPermission("Payments.Approve", "Approve");
            group.AddPermission("Payments.Create", "Create");

            var reports = context.AddGroup("Reports", "Reports");
            reports.AddPermission("Reports.Read", "Read");
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

    private sealed class NullCurrentUser : ICurrentUser
    {
        public bool IsAuthenticated => false;
        public Guid? Id => null;
        public Guid? TenantId => null;
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
}
