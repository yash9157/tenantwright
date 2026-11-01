using Identity.Application.ApplicationUsers;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Xunit;

namespace Identity.Tests;

public sealed class RoleMembershipPolicyTests
{
    [Fact]
    public async Task Policy_accepts_thirty_two_active_roles_and_rejects_the_thirty_third()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(
            dbContext,
            currentTenant,
            roleMembershipPolicy: new RoleMembershipPolicyOptions());
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var roleNames = Enumerable.Range(1, 33).Select(index => $"PlatformRole{index:D2}").ToArray();
        foreach (var roleName in roleNames)
        {
            var roleResult = await roleManager.CreateAsync(new ApplicationRole(roleName) { TenantId = null });
            Assert.True(roleResult.Succeeded);
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.Empty,
            UserName = "role.limit.user",
            Email = "role.limit.user@example.test"
        };
        var createResult = await userManager.CreateAsync(user, "Pass123$");
        Assert.True(createResult.Succeeded);

        var firstThirtyTwo = await userManager.AddToRolesAsync(user, roleNames.Take(32));
        var thirtyThird = await userManager.AddToRoleAsync(user, roleNames[32]);

        Assert.True(firstThirtyTwo.Succeeded);
        var error = Assert.Single(thirtyThird.Errors);
        Assert.Equal("active_role_membership_limit_exceeded", error.Code);
        Assert.Equal(32, (await userManager.GetRolesAsync(user)).Count);
    }
}
