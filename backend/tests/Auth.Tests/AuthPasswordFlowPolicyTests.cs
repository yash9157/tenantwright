using Auth.Authorization;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Auth.Tests;

/// <summary>
/// Regression guard: the resource-owner-password grant must never be enabled outside
/// Development, and an absent setting must resolve to disabled (fail-closed).
/// </summary>
public sealed class AuthPasswordFlowPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("anything-else")]
    public void ResolveEnabled_should_be_disabled_outside_development(string? configuredValue)
    {
        var configuration = BuildConfiguration(configuredValue);

        Assert.False(AuthPasswordFlowPolicy.ResolveEnabled(configuration, isDevelopment: false));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void ResolveEnabled_should_fail_startup_when_explicitly_enabled_outside_development(string configuredValue)
    {
        var configuration = BuildConfiguration(configuredValue);

        var exception = Assert.Throws<InvalidOperationException>(
            () => AuthPasswordFlowPolicy.ResolveEnabled(configuration, isDevelopment: false));

        Assert.Contains(AuthPasswordFlowPolicy.ConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void ResolveEnabled_should_be_enabled_in_development_unless_turned_off(string? configuredValue)
    {
        var configuration = BuildConfiguration(configuredValue);

        Assert.True(AuthPasswordFlowPolicy.ResolveEnabled(configuration, isDevelopment: true));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void ResolveEnabled_should_be_disabled_in_development_when_turned_off(string configuredValue)
    {
        var configuration = BuildConfiguration(configuredValue);

        Assert.False(AuthPasswordFlowPolicy.ResolveEnabled(configuration, isDevelopment: true));
    }

    private static IConfiguration BuildConfiguration(string? allowPasswordFlow)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (allowPasswordFlow != null)
        {
            values[AuthPasswordFlowPolicy.ConfigurationKey] = allowPasswordFlow;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
