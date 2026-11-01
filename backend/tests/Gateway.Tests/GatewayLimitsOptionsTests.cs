using System.Text.Json;
using ApiGateway;
using Xunit;

namespace Gateway.Tests;

public sealed class GatewayLimitsOptionsTests
{
    [Fact]
    public void Defaults_preserve_the_approved_header_limits()
    {
        var options = new GatewayLimitsOptions();

        Assert.Equal(8 * 1024, options.MaxAuthorizationHeaderBytes);
        Assert.Equal(32 * 1024, options.MaxRequestHeadersTotalSize);
        Assert.True(GatewayLimitsOptions.IsValid(options));
    }

    [Fact]
    public void Checked_in_configuration_preserves_the_approved_header_limits()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "gateway", "ApiGateway", "appsettings.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var limits = document.RootElement.GetProperty(GatewayLimitsOptions.SectionName);

        Assert.Equal(
            GatewayLimitsOptions.RequiredMaxAuthorizationHeaderBytes,
            limits.GetProperty(nameof(GatewayLimitsOptions.MaxAuthorizationHeaderBytes)).GetInt32());
        Assert.Equal(
            GatewayLimitsOptions.RequiredMaxRequestHeadersTotalSize,
            limits.GetProperty(nameof(GatewayLimitsOptions.MaxRequestHeadersTotalSize)).GetInt32());
    }

    [Theory]
    [InlineData(8191, 32768)]
    [InlineData(8193, 32768)]
    [InlineData(8192, 32767)]
    [InlineData(8192, 32769)]
    public void Validation_rejects_drift_from_the_approved_header_limits(int authorizationHeaderBytes, int headersTotalSize)
    {
        var options = new GatewayLimitsOptions
        {
            MaxAuthorizationHeaderBytes = authorizationHeaderBytes,
            MaxRequestHeadersTotalSize = headersTotalSize
        };

        Assert.False(GatewayLimitsOptions.IsValid(options));
        Assert.Throws<InvalidOperationException>(() => GatewayLimitsOptions.EnsureValid(options));
    }
}
