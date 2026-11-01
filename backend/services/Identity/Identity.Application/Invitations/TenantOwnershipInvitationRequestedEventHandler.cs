using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace Identity.Application.Invitations;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantOwnershipInvitationRequestedEto>),
    IncludeSelf = true)]
public sealed class TenantOwnershipInvitationRequestedEventHandler
    : ITenantWrightMessageHandler<TenantOwnershipInvitationRequestedEto>,
        ITransientDependency
{
    private readonly IOwnershipInvitationProvisioner _invitationProvisioner;

    public TenantOwnershipInvitationRequestedEventHandler(IOwnershipInvitationProvisioner invitationProvisioner)
    {
        _invitationProvisioner = invitationProvisioner;
    }

    public Task HandleAsync(
        TenantOwnershipInvitationRequestedEto eventData,
        CancellationToken cancellationToken = default)
        => _invitationProvisioner.CreateOwnershipInvitationAsync(eventData, cancellationToken);
}

[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantOwnershipInvitationCanceledEto>),
    IncludeSelf = true)]
public sealed class TenantOwnershipInvitationCanceledEventHandler
    : ITenantWrightMessageHandler<TenantOwnershipInvitationCanceledEto>,
        ITransientDependency
{
    private readonly IOwnershipInvitationProvisioner _invitationProvisioner;

    public TenantOwnershipInvitationCanceledEventHandler(IOwnershipInvitationProvisioner invitationProvisioner)
    {
        _invitationProvisioner = invitationProvisioner;
    }

    public Task HandleAsync(
        TenantOwnershipInvitationCanceledEto eventData,
        CancellationToken cancellationToken = default)
        => _invitationProvisioner.CancelOwnershipInvitationAsync(eventData, cancellationToken);
}
