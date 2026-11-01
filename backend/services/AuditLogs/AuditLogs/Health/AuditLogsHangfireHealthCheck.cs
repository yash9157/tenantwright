using Hangfire;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Hangfire;

namespace AuditLogs.Health;

public sealed class AuditLogsHangfireHealthCheck : IHealthCheck
{
    private readonly IOptions<BackgroundTasksHangfireOptions> _hangfireOptions;
    private readonly IOptions<AuditLogsOperationalHealthOptions> _healthOptions;

    public AuditLogsHangfireHealthCheck(
        IOptions<BackgroundTasksHangfireOptions> hangfireOptions,
        IOptions<AuditLogsOperationalHealthOptions> healthOptions)
    {
        _hangfireOptions = hangfireOptions;
        _healthOptions = healthOptions;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var options = _hangfireOptions.Value;
        if (!options.Enabled)
        {
            AuditLogsOperationalHealthTelemetry.RecordHangfire(false);
            return Task.FromResult(HealthCheckResult.Unhealthy("AuditLogs background processing is disabled."));
        }

        try
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-Math.Clamp(
                _healthOptions.Value.HangfireHeartbeatGraceSeconds,
                30,
                600));
            var requiredQueues = options.Queues.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var serverHealthy = JobStorage.Current
                .GetMonitoringApi()
                .Servers()
                .Any(server =>
                    server.Heartbeat >= cutoff &&
                    server.Queues != null &&
                    requiredQueues.IsSubsetOf(server.Queues));
            AuditLogsOperationalHealthTelemetry.RecordHangfire(serverHealthy);

            return Task.FromResult(serverHealthy
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("No current AuditLogs background server owns all configured queues."));
        }
        catch (Exception exception)
        {
            AuditLogsOperationalHealthTelemetry.RecordHangfire(false);
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "AuditLogs background server state could not be read.",
                exception));
        }
    }
}
