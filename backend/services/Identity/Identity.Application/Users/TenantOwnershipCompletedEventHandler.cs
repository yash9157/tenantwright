using Identity.Application.Contracts.Users;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace Identity.Application.Users;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantOwnershipCompletedEto>),
    IncludeSelf = true)]
public sealed class TenantOwnershipCompletedEventHandler
    : ITenantWrightMessageHandler<TenantOwnershipCompletedEto>,
        ITransientDependency
{
    private readonly IIdentitySessionRevocationService _sessionRevocationService;

    public TenantOwnershipCompletedEventHandler(IIdentitySessionRevocationService sessionRevocationService)
    {
        _sessionRevocationService = sessionRevocationService;
    }

    public Task HandleAsync(
        TenantOwnershipCompletedEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (!eventData.PreviousOwnerUserId.HasValue ||
            eventData.PreviousOwnerUserId == eventData.NewOwnerUserId)
        {
            return Task.CompletedTask;
        }

        return _sessionRevocationService.RevokeAllAsync(
            eventData.PreviousOwnerUserId.Value,
            eventData.TenantId,
            action: "TenantOwnershipTransferred",
            revocationSource: "TenantOwnershipTransfer",
            revokedByUserId: eventData.NewOwnerUserId,
            revocationReason: "Tenant ownership transferred",
            cancellationToken: cancellationToken);
    }
}
