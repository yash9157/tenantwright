using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Tenant.Application.Tenants;

namespace Tenant.Workers;

public sealed class TenantOnboardingWorkflowHealthCheck(
    TenantOnboardingWorkflowReconciler reconciler,
    IOptions<TenantOnboardingWorkflowReconciliationOptions> options) : IHealthCheck
{
    private static readonly Meter Meter = new("TenantWright.Tenant.WorkflowRecovery");
    private static readonly Histogram<int> Pending = Meter.CreateHistogram<int>("tenant.workflow.pending_sample");

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled) return HealthCheckResult.Degraded("Onboarding workflow recovery is disabled.");
        var threshold = options.Value.BacklogWarningThreshold;
        var count = await reconciler.CountPendingAsync(threshold + 1, cancellationToken);
        Pending.Record(count);
        return count > threshold
            ? HealthCheckResult.Degraded("Onboarding workflow recovery backlog exceeds its bounded threshold.")
            : HealthCheckResult.Healthy();
    }
}
