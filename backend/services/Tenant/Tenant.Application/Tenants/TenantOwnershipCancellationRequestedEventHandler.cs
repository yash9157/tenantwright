using Microsoft.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Tenant.Domain.Shared.Enums;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;

namespace Tenant.Application.Tenants;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantOwnershipCancellationRequestedEto>),
    IncludeSelf = true)]
public sealed class TenantOwnershipCancellationRequestedEventHandler(
    IDbContextProvider<TenantDbContextApp> dbContextProvider,
    ITenantWrightMessageSender messages)
    : ITenantWrightMessageHandler<TenantOwnershipCancellationRequestedEto>,
        ITransientDependency
{
    public async Task HandleAsync(
        TenantOwnershipCancellationRequestedEto command,
        CancellationToken cancellationToken = default)
    {
        if (command.RequestId == Guid.Empty ||
            command.TenantId == Guid.Empty ||
            !string.Equals(command.ReasonCode, "initial-owner-invitation-expired", StringComparison.Ordinal))
        {
            return;
        }

        var dbContext = await dbContextProvider.GetDbContextAsync();
        var request = await dbContext.TenantOwnershipRequests
            .FirstOrDefaultAsync(
                item => item.Id == command.RequestId && item.TenantId == command.TenantId,
                cancellationToken);
        if (request is null ||
            request.Kind != TenantOwnershipRequestKind.InitialOwner ||
            request.Status is TenantOwnershipRequestStatus.Completed or
                TenantOwnershipRequestStatus.Canceled or
                TenantOwnershipRequestStatus.Expired)
        {
            return;
        }

        request.ExpireIfDue(command.OccurredAtUtc);
        if (request.Status != TenantOwnershipRequestStatus.Expired)
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await messages.PublishEventAsync(
            new TenantOwnershipInvitationCanceledEto
            {
                RequestId = request.Id,
                TenantId = request.TenantId,
                OccurredAtUtc = command.OccurredAtUtc
            },
            cancellationToken: cancellationToken);
    }
}
