using Identity.Application.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Xunit;

namespace Identity.Tests;

public class TenantWrightIdentityRoleNamesTests
{
    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("tenantadmin")]
    public void Normalize_should_map_canonical_tenant_admin_name(string roleName)
    {
        var normalized = TenantWrightIdentityRoleNames.Normalize(roleName);

        Assert.Equal(TenantWrightIdentityRoleNames.TenantAdmin, normalized);
    }

    [Fact]
    public void Normalize_should_not_treat_workspace_admin_as_tenant_admin()
    {
        Assert.Equal("WorkspaceAdmin", TenantWrightIdentityRoleNames.Normalize("WorkspaceAdmin"));
    }

    [Fact]
    public void NormalizeMany_should_preserve_noncanonical_role_names()
    {
        var normalized = TenantWrightIdentityRoleNames.NormalizeMany(
            [TenantWrightIdentityRoleNames.TenantAdmin, "WorkspaceAdmin", "admin"]);

        Assert.Equal(
            [TenantWrightIdentityRoleNames.TenantAdmin, "WorkspaceAdmin", TenantWrightIdentityRoleNames.HostAdmin],
            normalized);
    }

    [Fact]
    public void Tenant_admin_identifier_should_be_stable_and_tenant_scoped()
    {
        var firstTenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var first = IdentitySystemRoleIds.TenantAdmin(firstTenant);

        Assert.Equal(first, IdentitySystemRoleIds.TenantAdmin(firstTenant));
        Assert.NotEqual(first, IdentitySystemRoleIds.TenantAdmin(secondTenant));
        Assert.NotEqual(Guid.Empty, IdentitySystemRoleIds.HostAdmin);
    }
}
