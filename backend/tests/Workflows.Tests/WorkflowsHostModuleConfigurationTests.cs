using System;
using System.Reflection;
using Workflows;
using Workflows.Application;
using Workflows.Application.Temporal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.Context;
using Xunit;

namespace Workflows.Tests;

public class WorkflowsHostModuleConfigurationTests
{
    [Fact]
    public void ConfigureServices_should_register_every_temporal_worker_activity()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        new WorkflowsApplicationModule().ConfigureServices(
            new ServiceConfigurationContext(services, configuration));

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(InitialOwnerOnboardingActivities) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void ValidateFailClosedSettings_should_reject_non_https_authority()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            InvokeValidateFailClosedSettings(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AuthServer:Authority"] = "http://localhost:5234/",
                    ["ConnectionStrings:Default"] = "Host=localhost;Database=WorkflowsDb;Username=postgres;Password=1234"
                })
                .Build()));

        Assert.Contains("AuthServer:Authority", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateFailClosedSettings_should_reject_missing_default_connection_string()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            InvokeValidateFailClosedSettings(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AuthServer:Authority"] = "https://localhost:7239/"
                })
                .Build()));

        Assert.Contains("ConnectionStrings:Default", ex.Message, StringComparison.Ordinal);
    }

    private static void InvokeValidateFailClosedSettings(IConfiguration configuration)
    {
        var method = typeof(WorkflowsHostModule).GetMethod(
            "ValidateFailClosedSettings",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        try
        {
            method!.Invoke(null, [configuration, false]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
