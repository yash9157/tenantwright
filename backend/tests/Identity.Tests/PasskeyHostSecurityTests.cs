using System.Reflection;
using System.Security.Claims;
using Identity.Application.Contracts.Passkeys;
using Identity.Controllers.Profile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Shared.MultiTenancy;
using Xunit;

namespace Identity.Tests;

public sealed class PasskeyHostSecurityTests
{
    [Fact]
    public void Profile_controller_should_apply_authenticated_passkey_rate_limit()
    {
        var attribute = Assert.Single(
            typeof(PasskeysController)
                .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true));

        Assert.Equal("identity-passkeys", attribute.PolicyName);
    }

    [Fact]
    public void Authenticated_rate_limit_partition_should_include_subject_tenant_ip_and_path()
    {
        var tenantId = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection()
            .AddSingleton<ICurrentTenant>(new IdentityTestSupport.TestCurrentTenant(tenantId))
            .BuildServiceProvider();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(OpenIddictConstants.Claims.Subject, "user-123")
        ], "test"));
        context.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString("D");
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        context.Request.Path = "/api/identity/profile/passkeys/registration/options";

        var key = IdentityHostModule.BuildAuthenticatedIdentityPartitionKey(
            context,
            "identity-passkeys");

        Assert.Equal(
            $"identity-passkeys:{tenantId:D}:192.0.2.10:/api/identity/profile/passkeys/registration/options:user-123",
            key);
    }

    [Fact]
    public void Production_passkey_configuration_should_reject_localhost()
    {
        var options = CreateOptions(
            "localhost",
            "https://localhost:7239");

        Assert.False(IdentityHostRegistrationExtensions.IsProductionPasskeyConfigurationValid(options));
    }

    [Fact]
    public void Production_passkey_configuration_should_reject_non_https_origin()
    {
        var options = CreateOptions(
            "auth.example.test",
            "http://auth.example.test");

        Assert.False(IdentityHostRegistrationExtensions.IsProductionPasskeyConfigurationValid(options));
    }

    [Theory]
    [InlineData("https://auth.example.test/passkey")]
    [InlineData("https://auth.example.test?source=test")]
    [InlineData("https://auth.example.test#fragment")]
    [InlineData("https://user@auth.example.test")]
    [InlineData("https://unrelated.example.test")]
    public void Production_passkey_configuration_should_reject_non_origin_or_unrelated_values(
        string origin)
    {
        var options = CreateOptions(
            "auth.example.test",
            origin);

        Assert.False(IdentityHostRegistrationExtensions.IsProductionPasskeyConfigurationValid(options));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://auth.example.test")]
    [InlineData("192.0.2.10")]
    public void Production_passkey_configuration_should_reject_invalid_relying_party_id(
        string relyingPartyId)
    {
        var options = CreateOptions(
            relyingPartyId,
            "https://auth.example.test");

        Assert.False(IdentityHostRegistrationExtensions.IsProductionPasskeyConfigurationValid(options));
    }

    [Fact]
    public void Production_passkey_configuration_should_accept_explicit_https_origin()
    {
        var options = CreateOptions(
            "auth.example.test",
            "https://auth.example.test");

        Assert.True(IdentityHostRegistrationExtensions.IsProductionPasskeyConfigurationValid(options));
    }

    [Fact]
    public void Production_passkey_configuration_should_accept_rp_subdomain_origin()
    {
        var options = CreateOptions(
            "example.test",
            "https://auth.example.test");

        Assert.True(IdentityHostRegistrationExtensions.IsProductionPasskeyConfigurationValid(options));
    }

    private static PasskeyOptions CreateOptions(
        string relyingPartyId,
        string origin)
    {
        return new PasskeyOptions
        {
            Enabled = true,
            RelyingPartyId = relyingPartyId,
            RelyingPartyName = "TenantWright",
            Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                origin
            },
            CeremonyTtlSeconds = 300
        };
    }
}
