using Identity.Application.Contracts.Users;
using Identity.Application.Users;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Identity.Tests;

public sealed class InternalUserProvisioningAppServiceTests
{
    [Fact]
    public async Task ProvisionFirstUserAsync_UsesCanonicalTenantAdminRoleId()
    {
        var tenant = new IdentityTestSupport.TestCurrentTenant(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "tenantwright");
        await using var dbContext = IdentityTestSupport.CreateDbContext(tenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, tenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, tenant);
        var service = new InternalUserProvisioningAppService(
            userManager,
            roleManager,
            tenant,
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator());

        var result = await service.ProvisionFirstUserAsync(new ProvisionFirstUserInput
        {
            UserName = "tenantadmin",
            Email = "tenantadmin@example.test",
            Password = "Abc123!x",
            RoleName = "TenantAdmin"
        });

        Assert.True(result.Succeeded, result.Error);
        var role = await dbContext.Roles.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(IdentitySystemRoleIds.TenantAdmin(tenant.Id!.Value), role.Id);
    }
}
