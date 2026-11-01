using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Temporalio.Worker;

namespace Workflows.Application.Temporal;

public sealed class TemporalWorkflowWorker(
    ITenantWrightTemporalClientProvider clients,
    IOptions<TemporalWorkflowOptions> options,
    InitialOwnerOnboardingActivities onboardingActivities) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !options.Value.WorkerEnabled)
            return;

        var client = await clients.GetAsync(stoppingToken);
        using var workflowWorker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(options.Value.WorkflowTaskQueue)
            {
                MaxConcurrentWorkflowTasks = options.Value.MaxConcurrentWorkflowTasks,
                GracefulShutdownTimeout = options.Value.GracefulShutdownTimeout
            }.AddWorkflow<InitialOwnerOnboardingWorkflow>());
        using var activityWorker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(options.Value.ActivityTaskQueue)
            {
                MaxConcurrentActivities = options.Value.MaxConcurrentActivities,
                GracefulShutdownTimeout = options.Value.GracefulShutdownTimeout
            }.AddActivity(onboardingActivities.RequestExpirationAsync));
        await Task.WhenAll(
            workflowWorker.ExecuteAsync(stoppingToken),
            activityWorker.ExecuteAsync(stoppingToken));
    }
}
