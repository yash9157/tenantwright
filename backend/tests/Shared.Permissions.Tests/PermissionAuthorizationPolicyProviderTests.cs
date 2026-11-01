using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Shared.Permissions.Authorization;
using Shared.Permissions.Client;
using Xunit;

namespace Shared.Permissions.Tests;

public class PermissionAuthorizationPolicyProviderTests
{
    [Fact]
    public async Task Returns_PermissionPolicy_When_NotDefined_And_Enabled()
    {
        var authOptions = Options.Create(new AuthorizationOptions());
        var permissionOptions = Options.Create(new SharedPermissionClientOptions { Enabled = true });
        var provider = new PermissionAuthorizationPolicyProvider(authOptions, permissionOptions);

        var policy = await provider.GetPolicyAsync("Project.TenantWright.Identity.Users.Read");

        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, r => r is PermissionAuthorizationRequirement req &&
                                                  req.PermissionName == "Project.TenantWright.Identity.Users.Read");
    }

    [Fact]
    public async Task Returns_Null_When_Disabled_And_NotDefined()
    {
        var authOptions = Options.Create(new AuthorizationOptions());
        var permissionOptions = Options.Create(new SharedPermissionClientOptions { Enabled = false });
        var provider = new PermissionAuthorizationPolicyProvider(authOptions, permissionOptions);

        var policy = await provider.GetPolicyAsync("Project.TenantWright.Identity.Users.Read");

        Assert.Null(policy);
    }
}
