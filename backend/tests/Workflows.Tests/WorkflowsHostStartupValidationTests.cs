using System.Reflection;
using Microsoft.Extensions.Configuration;
using Workflows;
using Xunit;

namespace Workflows.Tests;

public class WorkflowsHostStartupValidationTests
{
    [Fact]
    public void ValidateFailClosedSettings_Should_Reject_NonHttps_Authority_Outside_Development()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AuthServer:Authority"] = "http://auth.local/",
            ["ConnectionStrings:Default"] = "Host=localhost;Database=workflows;"
        });

        var ex = Assert.Throws<InvalidOperationException>(() => InvokeValidateFailClosedSettings(configuration, isDevelopment: false));
        Assert.Contains("AuthServer:Authority", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateFailClosedSettings_Should_Reject_Missing_Default_Connection_Outside_Development()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AuthServer:Authority"] = "https://auth.local/"
        });

        var ex = Assert.Throws<InvalidOperationException>(() => InvokeValidateFailClosedSettings(configuration, isDevelopment: false));
        Assert.Contains("ConnectionStrings:Default", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateFailClosedSettings_Should_Accept_Valid_Config_Outside_Development()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AuthServer:Authority"] = "https://auth.local/",
            ["ConnectionStrings:Default"] = "Host=localhost;Database=workflows;"
        });

        InvokeValidateFailClosedSettings(configuration, isDevelopment: false);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static void InvokeValidateFailClosedSettings(IConfiguration configuration, bool isDevelopment)
    {
        var method = typeof(WorkflowsHostModule).GetMethod(
            "ValidateFailClosedSettings",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        try
        {
            method!.Invoke(null, [configuration, isDevelopment]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
