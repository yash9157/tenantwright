using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Roles;
using Identity.Application.Roles;
using Identity.Application.Users;
using Identity.Controllers.Roles;
using Microsoft.AspNetCore.Authorization;
using Shared.Permissions.Definitions.Identity;
using Xunit;

namespace Identity.Tests;

/// <summary>
/// Locks the tenant-role read-authorization correction (reads must use the
/// `Identity.Roles` family, not `Identity.Users`) and the additive role metadata
/// round-trip (Description / Category / IsSensitive).
/// </summary>
public sealed class RolesControllerPolicyAndMetadataTests
{
    [Theory]
    [InlineData(nameof(RolesController.GetAsync), "Identity.Roles")]
    [InlineData(nameof(RolesController.GetListAsync), "Identity.Roles")]
    [InlineData(nameof(RolesController.CreateAsync), "Identity.Roles.Create")]
    [InlineData(nameof(RolesController.UpdateAsync), "Identity.Roles.Update")]
    [InlineData(nameof(RolesController.DeleteAsync), "Identity.Roles.Delete")]
    public void Role_endpoints_enforce_roles_family_policies(string methodName, string expectedPolicy)
    {
        var method = typeof(RolesController).GetMethod(methodName);
        Assert.NotNull(method);
        var authorize = method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single(a => a.Policy != null);
        Assert.Equal(expectedPolicy, authorize.Policy);
    }

    [Fact]
    public void Role_reads_do_not_use_the_users_policy()
    {
        foreach (var name in new[] { nameof(RolesController.GetAsync), nameof(RolesController.GetListAsync) })
        {
            var method = typeof(RolesController).GetMethod(name)!;
            var policies = method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>().Select(a => a.Policy).ToArray();
            Assert.DoesNotContain(IdentityPermissions.Users.Default, policies);
            Assert.Contains(IdentityPermissions.Roles.Default, policies);
        }
    }

    [Fact]
    public async Task Role_metadata_round_trips_through_create_and_get()
    {
        var service = CreateRoleService(out _);

        var created = await service.CreateAsync(new RoleCreateDto
        {
            Name = "DocumentReviewer",
            Description = "Reviews shared documents",
            Category = "Documents",
            IsSensitive = true
        });

        Assert.Equal("Reviews shared documents", created.Description);
        Assert.Equal("Documents", created.Category);
        Assert.True(created.IsSensitive);

        var fetched = await service.GetAsync(created.Id);
        Assert.Equal("Reviews shared documents", fetched.Description);
        Assert.Equal("Documents", fetched.Category);
        Assert.True(fetched.IsSensitive);
    }

    [Fact]
    public async Task Role_metadata_defaults_are_null_and_false_when_omitted()
    {
        var service = CreateRoleService(out _);

        var created = await service.CreateAsync(new RoleCreateDto { Name = "Basic" });

        Assert.Null(created.Description);
        Assert.Null(created.Category);
        Assert.False(created.IsSensitive);
    }

    private static RoleAppService CreateRoleService(out Guid tenantId)
    {
        tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId);
        var currentUser = new IdentityTestSupport.TestCurrentUser(
            Guid.NewGuid(), tenantId, "tenant.admin", TenantWrightIdentityRoleNames.TenantAdmin);
        var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager);
        return new RoleAppService(
            roleManager,
            currentTenant,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            guard);
    }
}
