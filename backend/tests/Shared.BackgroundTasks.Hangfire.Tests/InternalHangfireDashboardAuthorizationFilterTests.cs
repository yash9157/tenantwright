using System.Security.Claims;
using Shared.BackgroundTasks.Hangfire;
using Xunit;

namespace Shared.BackgroundTasks.Hangfire.Tests;

public class InternalHangfireDashboardAuthorizationFilterTests
{
    [Fact]
    public void Authorize_WhenUnauthenticated_ReturnsFalse()
    {
        var filter = new InternalHangfireDashboardAuthorizationFilter(["Ops"], []);

        Assert.False(filter.AuthorizePrincipal(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void Authorize_WhenRoleAllowed_ReturnsTrue()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Ops")],
            authenticationType: "Test",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);
        var filter = new InternalHangfireDashboardAuthorizationFilter(["Ops"], []);

        Assert.True(filter.AuthorizePrincipal(new ClaimsPrincipal(identity)));
    }

    [Fact]
    public void Authorize_WhenAuthenticatedButNotAllowed_ReturnsFalse()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Support")],
            authenticationType: "Test",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);
        var filter = new InternalHangfireDashboardAuthorizationFilter(["Ops"], ["trusted-client"]);

        Assert.False(filter.AuthorizePrincipal(new ClaimsPrincipal(identity)));
    }

    [Fact]
    public void Authorize_WhenClientIdAllowed_ReturnsTrue()
    {
        var identity = new ClaimsIdentity(
            [new Claim("client_id", "trusted-client")],
            authenticationType: "Test",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);
        var filter = new InternalHangfireDashboardAuthorizationFilter([], ["trusted-client"]);

        Assert.True(filter.AuthorizePrincipal(new ClaimsPrincipal(identity)));
    }
}
