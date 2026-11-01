using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Workers;
using Tenant.Application.Tenants;

namespace Tenant.Workers;

public sealed class TenantOnboardingWorkflowReconciliationWorker(
    TenantOnboardingWorkflowReconciler reconciler,
    IOptionsMonitor<TenantOnboardingWorkflowReconciliationOptions> options,
    ILogger<TenantOnboardingWorkflowReconciliationWorker> logger) : IPeriodicBackgroundWorker
{
    public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled) return BackgroundWorkerExecutionResult.Idle;
        var count = await reconciler.ReconcileAsync(settings.BatchSize, context.CancellationToken);
        if (count > 0) logger.LogInformation("Reconciled {OnboardingRequestCount} onboarding workflow states.", count);
        return count == settings.BatchSize ? BackgroundWorkerExecutionResult.WorkCompleted : BackgroundWorkerExecutionResult.Idle;
    }
}

public sealed class TenantOnboardingWorkflowReconciliationOptions : BackgroundWorkerOptions
{
    public int BatchSize { get; set; } = 50;
    public int BacklogWarningThreshold { get; set; } = 100;
}
