using Microsoft.Extensions.Configuration;
using Shared.BackgroundTasks.Hangfire;
using Xunit;

namespace Shared.BackgroundTasks.Hangfire.Tests;

public class BackgroundTasksHangfireOptionsTests
{
    [Fact]
    public void Normalize_WhenQueuesMissing_AddsServiceScopedQueues()
    {
        var options = new BackgroundTasksHangfireOptions
        {
            Enabled = true,
            ServiceName = "Identity Service"
        };

        BackgroundTasksHangfireOptionsPostConfigure.Normalize(options, isDevelopment: true);

        Assert.Equal("identity_service", options.ServiceName);
        Assert.Equal("hangfire_identity_service", options.SchemaName);
        Assert.Contains("identity_service_critical", options.Queues);
        Assert.True(options.PrepareSchemaIfNecessary);
    }

    [Fact]
    public void NormalizeQueue_WhenTooLong_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => BackgroundTasksHangfireOptionsPostConfigure.NormalizeQueue(new string('x', 51)));

        Assert.Contains("exceeds 50 characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WhenEnabledWithoutConnectionString_Throws()
    {
        var options = new BackgroundTasksHangfireOptions
        {
            Enabled = true,
            ServiceName = "identity"
        };
        BackgroundTasksHangfireOptionsPostConfigure.Normalize(options, isDevelopment: false);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate(configuration));

        Assert.Contains("requires connection string", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WhenDashboardEnabledWithoutAllowList_Throws()
    {
        var options = new BackgroundTasksHangfireOptions
        {
            Enabled = true,
            ServiceName = "identity",
            Dashboard = new BackgroundTasksHangfireDashboardOptions { Enabled = true }
        };
        BackgroundTasksHangfireOptionsPostConfigure.Normalize(options, isDevelopment: false);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=test"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate(configuration));

        Assert.Contains("AllowedRoles or AllowedClientIds", exception.Message, StringComparison.Ordinal);
    }
}
