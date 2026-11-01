using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Workflows.Application.Temporal;

public sealed class TemporalWorkflowOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<TemporalWorkflowOptions>
{
    public ValidateOptionsResult Validate(string? name, TemporalWorkflowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.TargetHost) || !options.TargetHost.Contains(':', StringComparison.Ordinal))
            failures.Add("TargetHost must be a non-empty host:port value.");
        if (string.IsNullOrWhiteSpace(options.Namespace) || !options.Namespace.StartsWith("tenantwright-", StringComparison.Ordinal))
            failures.Add("Namespace must be an environment-owned 'tenantwright-<environment>' namespace.");
        if (!string.Equals(options.WorkflowTaskQueue, TemporalWorkflowOptions.DefaultWorkflowTaskQueue, StringComparison.Ordinal))
            failures.Add($"WorkflowTaskQueue must be exactly '{TemporalWorkflowOptions.DefaultWorkflowTaskQueue}'.");
        if (!string.Equals(options.ActivityTaskQueue, TemporalWorkflowOptions.DefaultActivityTaskQueue, StringComparison.Ordinal))
            failures.Add($"ActivityTaskQueue must be exactly '{TemporalWorkflowOptions.DefaultActivityTaskQueue}'.");
        if (string.Equals(options.WorkflowTaskQueue, options.ActivityTaskQueue, StringComparison.Ordinal))
            failures.Add("Workflow and activity task queues must be distinct.");
        if (options.GracefulShutdownTimeout < TimeSpan.FromSeconds(5) ||
            options.GracefulShutdownTimeout > TimeSpan.FromMinutes(2))
            failures.Add("GracefulShutdownTimeout must be between 5 seconds and 2 minutes.");

        var localEnvironment = environment.IsDevelopment() ||
                               string.Equals(environment.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase);
        if (!localEnvironment)
        {
            if (!options.UseTls)
                failures.Add("UseTls must be enabled outside Development and test environments.");
            ValidateAbsoluteFile(options.ServerRootCaCertificatePath, nameof(options.ServerRootCaCertificatePath), failures);
            ValidateAbsoluteFile(options.ClientCertificatePath, nameof(options.ClientCertificatePath), failures);
            ValidateAbsoluteFile(options.ClientPrivateKeyPath, nameof(options.ClientPrivateKeyPath), failures);
            ValidateAbsoluteFile(options.ApiKeyPath, nameof(options.ApiKeyPath), failures);
            if (string.IsNullOrWhiteSpace(options.TlsDomain))
                failures.Add("TlsDomain is required outside Development and test environments.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateAbsoluteFile(string? path, string propertyName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            failures.Add($"{propertyName} must identify an existing absolute secret-file path.");
    }
}
