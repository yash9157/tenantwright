using Auth.Controllers;
using Auth.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace Auth.Tests;

public sealed class AuthEndpointSecurityMetadataTests
{
    [Fact]
    public void OpenIddict_authorize_and_userinfo_endpoints_should_require_rate_limiting()
    {
        AssertEndpointRateLimit(
            typeof(AuthorizationController),
            nameof(AuthorizationController.AuthorizeAsync),
            "token");
        AssertEndpointRateLimit(
            typeof(UserInfoController),
            nameof(UserInfoController.GetUserInfoAsync),
            "token");
    }

    [Fact]
    public void Public_account_flow_pages_should_allow_anonymous_access()
    {
        Assert.Contains("/Account/Login", AuthRazorPageConventions.PublicAnonymousAccountPages);
        Assert.Contains("/Account/Mfa", AuthRazorPageConventions.PublicAnonymousAccountPages);
        Assert.Contains("/Account/ResetPassword", AuthRazorPageConventions.PublicAnonymousAccountPages);
        Assert.Contains("/Account/PublicRegistrationStatus", AuthRazorPageConventions.PublicAnonymousAccountPages);
        Assert.DoesNotContain("/Account/Logout", AuthRazorPageConventions.PublicAnonymousAccountPages);
        Assert.Equal(
            AuthRazorPageConventions.PublicAnonymousAccountPages.Count,
            AuthRazorPageConventions.PublicAnonymousAccountPages.Distinct(StringComparer.Ordinal).Count());
    }

    private static void AssertEndpointRateLimit(Type controllerType, string methodName, string policyName)
    {
        var method = controllerType.GetMethod(methodName);
        Assert.NotNull(method);

        var attribute = method!
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .Cast<EnableRateLimitingAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attribute);
        Assert.Equal(policyName, attribute!.PolicyName);
    }
}
