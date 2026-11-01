using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Workflows.Application.Temporal;

public interface ITemporalWorkflowStarter
{
    Task StartInitialOwnerOnboardingAsync(Shared.EventContracts.TenantInitialOwnerOnboardingRequestedEto command, CancellationToken cancellationToken = default);
    Task CompleteInitialOwnerOnboardingAsync(Shared.EventContracts.TenantInitialOwnerOnboardingCompletedEto command, CancellationToken cancellationToken = default);
}

public sealed class TemporalWorkflowStarter(
    ITenantWrightTemporalClientProvider clients,
    IOptions<TemporalWorkflowOptions> options) : ITemporalWorkflowStarter
{
    public async Task StartInitialOwnerOnboardingAsync(Shared.EventContracts.TenantInitialOwnerOnboardingRequestedEto command, CancellationToken cancellationToken = default)
    {
        Validate(command.TenantId, command.RequestId, "initial-owner onboarding");
        if (command.ExpiresAtUtc <= command.OccurredAtUtc)
            throw new InvalidOperationException("Initial-owner onboarding requires an expiry after its creation time.");

        var client = await clients.GetAsync(cancellationToken);
        await ExecuteReplaySafeAsync(client, WorkflowId(command.RequestId), () => client.StartWorkflowAsync(
            InitialOwnerOnboardingWorkflow.WorkflowType,
            [new InitialOwnerOnboardingWorkflowInput(command.RequestId, command.TenantId, command.ExpiresAtUtc)],
            StartOptions(WorkflowId(command.RequestId), cancellationToken)), cancellationToken);
    }

    public async Task CompleteInitialOwnerOnboardingAsync(Shared.EventContracts.TenantInitialOwnerOnboardingCompletedEto command, CancellationToken cancellationToken = default)
    {
        Validate(command.TenantId, command.RequestId, "initial-owner onboarding");
        var client = await clients.GetAsync(cancellationToken);
        var handle = client.GetWorkflowHandle(WorkflowId(command.RequestId));
        await ExecuteReplaySafeAsync(client, WorkflowId(command.RequestId), () =>
            handle.SignalAsync(InitialOwnerOnboardingWorkflow.CompletedSignal, Array.Empty<object>(),
                new WorkflowSignalOptions { Rpc = new() { CancellationToken = cancellationToken } }), cancellationToken);
    }





    private WorkflowOptions StartOptions(string id, CancellationToken cancellationToken = default) => new(id, options.Value.WorkflowTaskQueue)
    {
        IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
        IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
        Rpc = new() { CancellationToken = cancellationToken }
    };

    private static async Task ExecuteReplaySafeAsync(ITemporalClient client, string workflowId,
        Func<Task> operation, CancellationToken cancellationToken)
    {
        try
        {
            await operation();
        }
        catch (Exception exception) when (exception is WorkflowAlreadyStartedException ||
            exception is RpcException { Code: RpcException.StatusCode.NotFound })
        {
            // Missing starts must retry; failed/terminated runs require operator recovery.
            // Only a proved successful closed run makes late replay a safe no-op.
            var execution = await client.GetWorkflowHandle(workflowId).DescribeAsync(
                new WorkflowDescribeOptions { Rpc = new() { CancellationToken = cancellationToken } });
            if (execution.Status != WorkflowExecutionStatus.Completed) throw;
        }
    }

    private static void Validate(Guid? tenantId, Guid aggregateId, string aggregateName)
    {
        if (!tenantId.HasValue || tenantId.Value == Guid.Empty)
            throw new InvalidOperationException("Temporal workflows require a non-empty tenant id.");
        if (aggregateId == Guid.Empty)
            throw new InvalidOperationException($"Temporal {aggregateName} workflows require a non-empty aggregate id.");
    }

    private static string WorkflowId(Guid requestId) => $"tenant-initial-owner-{requestId:N}";
}
