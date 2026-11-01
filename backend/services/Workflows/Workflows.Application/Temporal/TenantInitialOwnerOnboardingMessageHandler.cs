using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace Workflows.Application.Temporal;

/// <summary>
/// Drives the Temporal initial-owner onboarding workflow from the Tenant commands that start and complete it.
/// </summary>
[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantInitialOwnerOnboardingRequestedEto>),
    typeof(ITenantWrightMessageHandler<TenantInitialOwnerOnboardingCompletedEto>),
    IncludeSelf = true)]
public sealed class TenantInitialOwnerOnboardingMessageHandler(ITemporalWorkflowStarter workflows)
    : ITenantWrightMessageHandler<TenantInitialOwnerOnboardingRequestedEto>,
        ITenantWrightMessageHandler<TenantInitialOwnerOnboardingCompletedEto>,
        ITransientDependency
{
    public Task HandleAsync(
        TenantInitialOwnerOnboardingRequestedEto message,
        CancellationToken cancellationToken) =>
        workflows.StartInitialOwnerOnboardingAsync(message, cancellationToken);

    public Task HandleAsync(
        TenantInitialOwnerOnboardingCompletedEto message,
        CancellationToken cancellationToken) =>
        workflows.CompleteInitialOwnerOnboardingAsync(message, cancellationToken);
}
