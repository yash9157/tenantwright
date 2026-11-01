using System.Reflection;
using Auth;
using Microsoft.Extensions.Configuration;
using Shared.AspNetCore.Host;
using Xunit;

namespace Auth.Tests;

public class AuthHostModuleKubernetesCompatibilityTests
{
    [Fact]
    public void ValidateInternalServiceUrl_Allows_Https_When_Flag_Disabled()
    {
        InvokeValidateInternalServiceUrl(
            "Services:Identity:Url",
            "https://identity.example.com/",
            allowInsecureInternalHttp: false);
    }

    [Fact]
    public void ValidateInternalServiceUrl_Allows_Internal_Http_When_Flag_Enabled()
    {
        InvokeValidateInternalServiceUrl(
            "Services:Identity:Url",
            "http://tenantwright-identity:8080/",
            allowInsecureInternalHttp: true);
    }

    [Fact]
    public void ValidateInternalServiceUrl_Rejects_External_Http_When_Flag_Enabled()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            InvokeValidateInternalServiceUrl(
                "Services:Identity:Url",
                "http://example.com/",
                allowInsecureInternalHttp: true));

        Assert.Contains("Services:AllowInsecureInternalHttp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateInternalServiceUrl_Rejects_Internal_Http_When_Flag_Disabled()
    {
        Assert.Throws<InvalidOperationException>(() =>
            InvokeValidateInternalServiceUrl(
                "Services:Tenant:Url",
                "http://tenantwright-tenant:8080/",
                allowInsecureInternalHttp: false));
    }

    [Fact]
    public void ValidatePublicBaseUrl_Allows_Absolute_Https()
    {
        InvokeValidatePublicBaseUrl("https://auth.example.com/");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://auth.example.com/")]
    [InlineData("/auth")]
    public void ValidatePublicBaseUrl_Rejects_Missing_NonHttps_Or_Relative_Url(string? url)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => InvokeValidatePublicBaseUrl(url));

        Assert.Contains("Auth:PublicBaseUrl", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedHostHttpsRedirection_Defaults_To_Enabled_And_Can_Be_Disabled()
    {
        var defaultConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();
        Assert.True(SharedAspNetCoreHostModule.IsHttpsRedirectionEnabled(defaultConfiguration));

        var disabledConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hosting:HttpsRedirection:Enabled"] = "false"
            })
            .Build();

        Assert.False(SharedAspNetCoreHostModule.IsHttpsRedirectionEnabled(disabledConfiguration));
    }

    private static void InvokeValidateInternalServiceUrl(string configurationKey, string url, bool allowInsecureInternalHttp)
    {
        var method = typeof(AuthHostModule).GetMethod(
            "ValidateInternalServiceUrl",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        try
        {
            method!.Invoke(null, [configurationKey, url, allowInsecureInternalHttp]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static void InvokeValidatePublicBaseUrl(string? url)
    {
        var method = typeof(AuthHostModule).GetMethod(
            "ValidatePublicBaseUrl",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        try
        {
            method!.Invoke(null, [url]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
