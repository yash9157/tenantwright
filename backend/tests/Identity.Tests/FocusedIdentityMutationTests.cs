using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Mutations;
using Identity.Application.Contracts.Platform;
using Identity.Application.Contracts.Users;
using Identity.Application.Platform;
using Identity.Application.Users;
using Identity.Controllers.Platform;
using Identity.Controllers.SecurityLogs;
using Identity.Controllers.Users;
using Identity.Domain.ApplicationUsers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.CurrentUser;
using Shared.Data.Exceptions;
using Shared.Permissions.Definitions.Identity;
using Xunit;

namespace Identity.Tests;

public sealed class FocusedIdentityMutationTests
{
    [Fact]
    public void Controllers_should_use_focused_mutation_permissions()
    {
        AssertPolicy<UsersController>(nameof(UsersController.SetRolesAsync), IdentityPermissions.Users.ManageRoles);
        AssertPolicy<UsersController>(nameof(UsersController.ActivateAsync), IdentityPermissions.Users.Activate);
        AssertPolicy<UsersController>(nameof(UsersController.DeactivateAsync), IdentityPermissions.Users.Deactivate);
        AssertPolicy<UsersController>(nameof(UsersController.LockAsync), IdentityPermissions.Users.Lock);
        AssertPolicy<UsersController>(nameof(UsersController.UnlockAsync), IdentityPermissions.Users.Unlock);

        AssertPolicy<PlatformTenantUsersController>(nameof(PlatformTenantUsersController.SetRolesAsync), IdentityPermissions.Users.ManageRoles);

        AssertPolicy<PlatformStaffController>(nameof(PlatformStaffController.SetRolesAsync), IdentityPermissions.PlatformStaff.ManageRoles);
        AssertPolicy<PlatformStaffController>(nameof(PlatformStaffController.ActivateAsync), IdentityPermissions.PlatformStaff.Activate);
        AssertPolicy<PlatformStaffController>(nameof(PlatformStaffController.DeactivateAsync), IdentityPermissions.PlatformStaff.Deactivate);
        AssertPolicy<PlatformStaffController>(nameof(PlatformStaffController.LockAsync), IdentityPermissions.PlatformStaff.Lock);
        AssertPolicy<PlatformStaffController>(nameof(PlatformStaffController.UnlockAsync), IdentityPermissions.PlatformStaff.Unlock);
        AssertPolicy<SecurityLogsController>(nameof(SecurityLogsController.GetDetailAsync), IdentityPermissions.SecurityLogs.ReadDetails);
    }

    [Fact]
    public async Task Tenant_profile_update_should_reject_access_changes_and_focused_commands_should_apply_them()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId);
        ICurrentUser currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin);
        await using var db = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(db, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(db, currentTenant);
        var roleResult = await roleManager.CreateAsync(new ApplicationRole("Reviewer") { TenantId = tenantId });
        Assert.True(roleResult.Succeeded, string.Join(", ", roleResult.Errors.Select(x => x.Description)));
        var revocations = new CapturingSessionRevocationService();
        var service = new UserAppService(
            userManager,
            IdentityTestSupport.AsDbContextProvider(db),
            currentTenant,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            revocations,
            new TenantUserManagementGuard(currentUser, currentTenant, userManager),
            currentUser,
            userImportExportCoordinator: null!,
            tenantStore: new IdentityTestSupport.EmptyTenantStore());

        var created = await service.CreateAsync(new UserCreateDto
        {
            UserName = "reviewer.one",
            Email = "reviewer.one@example.test",
            Password = "Pass123$"
        });

        var stateError = await Assert.ThrowsAsync<SharedValidationException>(() => service.UpdateAsync(created.Id, new UserUpdateDto
        {
            Email = created.Email,
            IsActive = false,
            RoleNames = []
        }));
        Assert.Contains("deactivate", stateError.Message, StringComparison.OrdinalIgnoreCase);

        var roleError = await Assert.ThrowsAsync<SharedValidationException>(() => service.UpdateAsync(created.Id, new UserUpdateDto
        {
            Email = created.Email,
            IsActive = true,
            RoleNames = ["Reviewer"]
        }));
        Assert.Contains("roles", roleError.Message, StringComparison.OrdinalIgnoreCase);

        var assigned = await service.SetRolesAsync(created.Id, new IdentityRoleAssignmentInput
        {
            RoleNames = ["Reviewer"],
            ConcurrencyStamp = created.ConcurrencyStamp,
            ReasonCode = "assign-reviewer-role",
            Note = "Approved staffing assignment"
        });
        Assert.Equal(["Reviewer"], assigned.Roles);

        var deactivated = await service.DeactivateAsync(created.Id, new IdentityActiveStateChangeInput
        {
            ConcurrencyStamp = assigned.ConcurrencyStamp,
            ReasonCode = "employment-hold",
            Note = "Temporary access hold"
        });
        Assert.False(deactivated.IsActive);
        Assert.Equal(2, revocations.Calls.Count);
        Assert.Contains(revocations.Calls, x => x.Action == "UserRolesChanged");
        Assert.Contains(revocations.Calls, x => x.Action == "UserDeactivated");
        Assert.Contains(revocations.Calls, x => x.Reason == "assign-reviewer-role");
        Assert.Contains(revocations.Calls, x => x.Reason == "employment-hold");

        await db.SaveChangesAsync();
        var roleLog = await db.IdentitySecurityLogs.SingleAsync(x => x.Action == "RolesChanged");
        Assert.Contains("\"reasonCode\":\"assign-reviewer-role\"", roleLog.ExtraProperties, StringComparison.Ordinal);
        Assert.Contains("\"note\":\"Approved staffing assignment\"", roleLog.ExtraProperties, StringComparison.Ordinal);
        var stateLog = await db.IdentitySecurityLogs.SingleAsync(x => x.Action == "Deactivated");
        Assert.Contains("\"reasonCode\":\"employment-hold\"", stateLog.ExtraProperties, StringComparison.Ordinal);
        Assert.Contains("\"note\":\"Temporary access hold\"", stateLog.ExtraProperties, StringComparison.Ordinal);

        await Assert.ThrowsAsync<SharedDbConcurrencyException>(() => service.ActivateAsync(
            created.Id,
            new IdentityActiveStateChangeInput
            {
                ConcurrencyStamp = assigned.ConcurrencyStamp,
                ReasonCode = "stale-reactivation"
            }));
    }

    [Fact]
    public async Task Platform_profile_update_should_reject_access_changes_and_focused_commands_should_apply_them()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        ICurrentUser currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin);
        await using var db = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(db, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(db, currentTenant);
        var roleResult = await roleManager.CreateAsync(new ApplicationRole("TenantSupport"));
        Assert.True(roleResult.Succeeded, string.Join(", ", roleResult.Errors.Select(x => x.Description)));
        var revocations = new CapturingSessionRevocationService();
        var service = new PlatformStaffAppService(
            userManager,
            IdentityTestSupport.AsDbContextProvider(db),
            currentTenant,
            currentUser,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            revocations);

        var created = await service.CreateAsync(new PlatformStaffCreateDto
        {
            UserName = "support.one",
            Email = "support.one@example.test",
            Password = "Pass123$"
        });

        await Assert.ThrowsAsync<SharedValidationException>(() => service.UpdateAsync(created.Id, new PlatformStaffUpdateDto
        {
            Email = created.Email,
            IsActive = false,
            RoleNames = []
        }));

        var assigned = await service.SetRolesAsync(created.Id, new IdentityRoleAssignmentInput
        {
            RoleNames = ["TenantSupport"],
            ConcurrencyStamp = created.ConcurrencyStamp,
            ReasonCode = "support-duty",
            Note = "Approved support rotation"
        });
        Assert.Equal(["TenantSupport"], assigned.Roles);

        var deactivated = await service.DeactivateAsync(created.Id, new IdentityActiveStateChangeInput
        {
            ConcurrencyStamp = assigned.ConcurrencyStamp,
            ReasonCode = "access-hold",
            Note = "Host access review"
        });
        Assert.False(deactivated.IsActive);
        Assert.Equal(2, revocations.Calls.Count);
        Assert.Contains(revocations.Calls, x => x.Reason == "support-duty");
        Assert.Contains(revocations.Calls, x => x.Reason == "access-hold");

        await db.SaveChangesAsync();
        var roleLog = await db.IdentitySecurityLogs.SingleAsync(x => x.Action == "PlatformStaffRolesChanged");
        Assert.Contains("\"reasonCode\":\"support-duty\"", roleLog.ExtraProperties, StringComparison.Ordinal);
        Assert.Contains("\"note\":\"Approved support rotation\"", roleLog.ExtraProperties, StringComparison.Ordinal);
        var stateLog = await db.IdentitySecurityLogs.SingleAsync(x => x.Action == "PlatformStaffDeactivated");
        Assert.Contains("\"reasonCode\":\"access-hold\"", stateLog.ExtraProperties, StringComparison.Ordinal);
        Assert.Contains("\"note\":\"Host access review\"", stateLog.ExtraProperties, StringComparison.Ordinal);

        await Assert.ThrowsAsync<SharedDbConcurrencyException>(() => service.ActivateAsync(
            created.Id,
            new IdentityActiveStateChangeInput
            {
                ConcurrencyStamp = assigned.ConcurrencyStamp,
                ReasonCode = "stale-reactivation"
            }));
    }

    private static void AssertPolicy<TController>(string methodName, string expectedPolicy)
    {
        var method = typeof(TController).GetMethod(methodName);
        Assert.NotNull(method);
        var policies = method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(x => x.Policy)
            .ToList();
        Assert.Contains(expectedPolicy, policies);
    }

    private sealed class CapturingSessionRevocationService : IIdentitySessionRevocationService
    {
        public List<(Guid UserId, Guid? TenantId, string Action, string? Reason)> Calls { get; } = [];

        public Task<int> RevokeAllAsync(Guid userId, Guid? tenantId, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((userId, tenantId, action, revocationReason));
            return Task.FromResult(1);
        }

        public Task<int> RevokeAllByTenantAsync(Guid tenantId, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<BulkRevokeSessionsResultDto> RevokeSelectedAsync(IReadOnlyCollection<Guid> sessionIds, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BulkRevokeSessionsResultDto());
    }
}
