using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Users;
using Identity.Application.Users;
using Shared.CurrentUser;
using Shared.MultiTenancy;
using Xunit;

namespace Identity.Tests;

/// <summary>
/// Behavioral tests for the tenant primary-admin summary read (design §8, Option A).
/// These exercise the real <see cref="UserAppService"/> method (via InternalsVisibleTo)
/// rather than reflection, covering the host-only gate and the empty-input guard.
/// </summary>
public sealed class TenantPrimaryAdminSummaryBehaviorTests
{
    [Fact]
    public async Task GetPrimaryAdmins_rejects_non_host_callers()
    {
        // A tenant-admin passes the general management actor check but must still be
        // rejected by the host-only cross-tenant gate.
        var tenantId = Guid.NewGuid();
        var service = CreateService(new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), tenantId, "tenant.admin", TenantWrightIdentityRoleNames.TenantAdmin), tenantId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetPrimaryAdminsAsync(new TenantPrimaryAdminsInput { TenantIds = [tenantId] }));
    }

    [Fact]
    public async Task GetPrimaryAdmins_returns_empty_for_empty_input()
    {
        var service = CreateService(new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), null, "platform.staff"));

        var result = await service.GetPrimaryAdminsAsync(new TenantPrimaryAdminsInput { TenantIds = [] });

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPrimaryAdmins_returns_empty_when_no_admins_match()
    {
        var service = CreateService(new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), null, "platform.staff"));

        // A host admin querying tenants that have no TenantAdmin users gets an empty,
        // single-query result (no per-tenant lookups).
        var result = await service.GetPrimaryAdminsAsync(
            new TenantPrimaryAdminsInput { TenantIds = [Guid.NewGuid(), Guid.NewGuid()] });

        Assert.Empty(result);
    }

    private static UserAppService CreateService(ICurrentUser currentUser, Guid? currentTenantId = null)
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(currentTenantId);
        var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);

        return new UserAppService(
            userManager,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            currentTenant,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            new IdentityTestSupport.NoopIdentitySessionRevocationService(),
            guard,
            currentUser,
            userImportExportCoordinator: null!,
            tenantStore: new IdentityTestSupport.EmptyTenantStore());
    }
}
