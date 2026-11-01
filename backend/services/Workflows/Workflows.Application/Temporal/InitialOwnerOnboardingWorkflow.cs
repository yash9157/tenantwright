using Temporalio.Activities;
using Temporalio.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Workflows.Application.Temporal;

public sealed record InitialOwnerOnboardingWorkflowInput(Guid RequestId, Guid TenantId, DateTime ExpiresAtUtc);

[Workflow("tenant-initial-owner-onboarding-v1")]
public sealed class InitialOwnerOnboardingWorkflow
{
    public const string WorkflowType = "tenant-initial-owner-onboarding-v1";
    public const string CompletedSignal = "tenant-initial-owner-onboarding-completed-v1";

    private bool _terminal;

    [WorkflowRun]
    public async Task RunAsync(InitialOwnerOnboardingWorkflowInput input)
    {
        if (_terminal) return;
        var remaining = input.ExpiresAtUtc - Workflow.UtcNow;
        if (remaining > TimeSpan.Zero && await Workflow.WaitConditionAsync(() => _terminal, remaining)) return;

        await Workflow.ExecuteActivityAsync(
            "request-tenant-initial-owner-expiration-v1",
            [input.RequestId, input.TenantId, input.ExpiresAtUtc],
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromMinutes(1),
                RetryPolicy = new() { MaximumAttempts = 5 }
            });

        await Workflow.WaitConditionAsync(() => _terminal);
    }

    [WorkflowSignal(CompletedSignal)]
    public Task OnOwnershipCompletedAsync()
    {
        _terminal = true;
        return Task.CompletedTask;
    }
}

public sealed class InitialOwnerOnboardingActivities(IServiceScopeFactory scopeFactory)
{
    [Activity("request-tenant-initial-owner-expiration-v1")]
    public async Task RequestExpirationAsync(Guid requestId, Guid tenantId, DateTime expiresAtUtc)
    {
        if (requestId == Guid.Empty || tenantId == Guid.Empty)
            throw new ArgumentException("Initial-owner expiration requires non-empty request and tenant identifiers.");

        using var scope = scopeFactory.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<Shared.EventBus.Messaging.ITenantWrightMessageSender>();
        await messages.SendCommandAsync(new Shared.EventContracts.TenantOwnershipCancellationRequestedEto
        {
            RequestId = requestId,
            TenantId = tenantId,
            ReasonCode = "initial-owner-invitation-expired",
            OccurredAtUtc = expiresAtUtc
        });
    }
}
