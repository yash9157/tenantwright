using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Workflows.Application.Temporal;
using Xunit;

namespace Workflows.Tests;

public class WorkflowEventContractTests
{

    [Fact]
    public void Temporal_options_allow_explicit_insecure_transport_only_in_testing()
    {
        var validator = new TemporalWorkflowOptionsValidator(new TestHostEnvironment("Testing"));
        var result = validator.Validate(null, new TemporalWorkflowOptions
        {
            Enabled = true,
            Namespace = "tenantwright-testing"
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Temporal_options_fail_closed_without_production_tls_and_identity_files()
    {
        var validator = new TemporalWorkflowOptionsValidator(new TestHostEnvironment(Environments.Production));
        var result = validator.Validate(null, new TemporalWorkflowOptions
        {
            Enabled = true,
            Namespace = "tenantwright-production"
        });

        Assert.False(result.Succeeded);
        var failures = Assert.IsAssignableFrom<IEnumerable<string>>(result.Failures);
        Assert.Contains(failures, failure => failure.Contains("UseTls", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("ApiKeyPath", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("ClientCertificatePath", StringComparison.Ordinal));
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Workflows.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
