using Permissions.Application.Contracts.Provisioning;
using Shared.EventContracts;
using Shared.EventBus.Messaging;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Provisioning;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantOwnershipCompletedEto>),
    IncludeSelf = true)]
public sealed class TenantOwnershipCompletedGrantProvisioningEventHandler
    : ITenantWrightMessageHandler<TenantOwnershipCompletedEto>,
        ITransientDependency
{
    private readonly ITenantAdminGrantProvisioningService _grantProvisioningService;
    private readonly ITenantWrightMessageSender _messages;

    public TenantOwnershipCompletedGrantProvisioningEventHandler(
        ITenantAdminGrantProvisioningService grantProvisioningService,
        ITenantWrightMessageSender messages)
    {
        _grantProvisioningService = grantProvisioningService;
        _messages = messages;
    }

    public async Task HandleAsync(
        TenantOwnershipCompletedEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.RequestId == Guid.Empty || eventData.TenantId == Guid.Empty ||
            eventData.NewOwnerUserId == Guid.Empty || eventData.PreviousOwnerUserId.HasValue)
            return;

        await _grantProvisioningService.UpsertTenantAdminGrantsAsync(
            eventData.TenantId,
            cancellationToken);
        await _messages.PublishEventAsync(new TenantAdminGrantsProvisionedEto
        {
            RequestId = eventData.RequestId,
            TenantId = eventData.TenantId,
            OwnerUserId = eventData.NewOwnerUserId,
            OccurredAtUtc = DateTime.UtcNow
        }, cancellationToken);
    }
}
