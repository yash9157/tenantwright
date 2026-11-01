using ApiGateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Gateway.Tests;

public class GatewaySecurityPolicyTests
{
    [Theory]
    [InlineData("/g1/internal/sessions")]
    [InlineData("/G12/INTERNAL")]
    [InlineData("/g3/integration-api/permissions")]
    [InlineData("/G9/Integration-Api")]
    public void IsBlockedPath_rejects_internal_and_integration_surfaces(string path)
    {
        Assert.True(GatewaySecurityPolicy.IsBlockedPath(new PathString(path)));
    }

    [Theory]
    [InlineData("/g1/users")]
    [InlineData("/internal/users")]
    [InlineData("/g1/public/integration-api")]
    [InlineData("/health")]
    public void IsBlockedPath_allows_public_and_non_gateway_paths(string path)
    {
        Assert.False(GatewaySecurityPolicy.IsBlockedPath(new PathString(path)));
    }

    [Fact]
    public void RemoveUntrustedHeaders_strips_tenant_and_internal_trust_headers()
    {
        var headers = new HeaderDictionary
        {
            ["X-Tenant-Id"] = Guid.NewGuid().ToString("D"),
            ["X-Internal-Request"] = "true",
            ["X-Internal-Client"] = "spoofed",
            ["X-Client-Certificate"] = "spoofed",
            ["X-Correlation-Id"] = "preserved"
        };

        GatewaySecurityPolicy.RemoveUntrustedHeaders(headers);

        Assert.False(headers.ContainsKey("X-Tenant-Id"));
        Assert.False(headers.ContainsKey("X-Internal-Request"));
        Assert.False(headers.ContainsKey("X-Internal-Client"));
        Assert.False(headers.ContainsKey("X-Client-Certificate"));
        Assert.Equal("preserved", headers["X-Correlation-Id"]);
    }

    [Fact]
    public void TryPromoteNotificationsHubAccessToken_promotes_a_single_hub_query_token()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/g5/hubs/notifications";
        context.Request.QueryString =
            new QueryString("?negotiateVersion=1&access_token=signalr-token");

        var promoted =
            GatewaySecurityPolicy.TryPromoteNotificationsHubAccessToken(context.Request);

        Assert.True(promoted);
        Assert.Equal("Bearer signalr-token", context.Request.Headers.Authorization);
        Assert.Equal("?negotiateVersion=1", context.Request.QueryString.Value);
    }

    [Theory]
    [InlineData("/g5/notifications")]
    [InlineData("/g4/hubs/notifications")]
    [InlineData("/g5/hubs/notifications-impersonator")]
    public void TryPromoteNotificationsHubAccessToken_rejects_non_hub_paths(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString("?access_token=signalr-token");

        var promoted =
            GatewaySecurityPolicy.TryPromoteNotificationsHubAccessToken(context.Request);

        Assert.False(promoted);
        Assert.False(context.Request.Headers.ContainsKey("Authorization"));
        Assert.Equal("?access_token=signalr-token", context.Request.QueryString.Value);
    }

    [Fact]
    public void TryPromoteNotificationsHubAccessToken_does_not_replace_an_authorization_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/g5/hubs/notifications";
        context.Request.Headers.Authorization = "Bearer existing-token";
        context.Request.QueryString = new QueryString("?access_token=signalr-token");

        var promoted =
            GatewaySecurityPolicy.TryPromoteNotificationsHubAccessToken(context.Request);

        Assert.False(promoted);
        Assert.Equal("Bearer existing-token", context.Request.Headers.Authorization);
        Assert.Equal("?access_token=signalr-token", context.Request.QueryString.Value);
    }

    [Fact]
    public void HasAuthorizationHeaderWithinLimit_allows_request_without_authorization_header()
    {
        var headers = new HeaderDictionary();

        var isWithinLimit = GatewaySecurityPolicy.HasAuthorizationHeaderWithinLimit(
            headers,
            GatewayLimitsOptions.RequiredMaxAuthorizationHeaderBytes);

        Assert.True(isWithinLimit);
    }

    [Fact]
    public void HasAuthorizationHeaderWithinLimit_allows_authorization_field_at_exact_byte_budget()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = new string(
                'a',
                GatewayLimitsOptions.RequiredMaxAuthorizationHeaderBytes - AuthorizationHeaderFieldOverheadBytes)
        };

        var isWithinLimit = GatewaySecurityPolicy.HasAuthorizationHeaderWithinLimit(
            headers,
            GatewayLimitsOptions.RequiredMaxAuthorizationHeaderBytes);

        Assert.True(isWithinLimit);
    }

    [Fact]
    public void HasAuthorizationHeaderWithinLimit_rejects_authorization_field_over_byte_budget()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = new string(
                'a',
                GatewayLimitsOptions.RequiredMaxAuthorizationHeaderBytes - AuthorizationHeaderFieldOverheadBytes + 1)
        };

        var isWithinLimit = GatewaySecurityPolicy.HasAuthorizationHeaderWithinLimit(
            headers,
            GatewayLimitsOptions.RequiredMaxAuthorizationHeaderBytes);

        Assert.False(isWithinLimit);
    }

    [Fact]
    public void HasAuthorizationHeaderWithinLimit_rejects_duplicate_authorization_values()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = new StringValues(["Bearer first", "Bearer second"])
        };

        var isWithinLimit = GatewaySecurityPolicy.HasAuthorizationHeaderWithinLimit(
            headers,
            GatewayLimitsOptions.RequiredMaxAuthorizationHeaderBytes);

        Assert.False(isWithinLimit);
    }

    [Fact]
    public void HasAuthorizationHeaderWithinLimit_counts_header_name_separator_and_crlf()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = new string('a', GatewayLimitsOptions.RequiredMaxAuthorizationHeaderBytes)
        };

        var isWithinLimit = GatewaySecurityPolicy.HasAuthorizationHeaderWithinLimit(
            headers,
            GatewayLimitsOptions.RequiredMaxAuthorizationHeaderBytes);

        Assert.False(isWithinLimit);
    }

    private static readonly int AuthorizationHeaderFieldOverheadBytes =
        System.Text.Encoding.UTF8.GetByteCount("Authorization: \r\n");
}
