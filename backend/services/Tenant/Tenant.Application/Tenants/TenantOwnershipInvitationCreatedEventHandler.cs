using Microsoft.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;

namespace Tenant.Application.Tenants;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantOwnershipInvitationCreatedEto>),
    IncludeSelf = true)]
public sealed class TenantOwnershipInvitationCreatedEventHandler
    : ITenantWrightMessageHandler<TenantOwnershipInvitationCreatedEto>,
        ITransientDependency
{
    private readonly IDbContextProvider<TenantDbContextApp> _dbContextProvider;

    public TenantOwnershipInvitationCreatedEventHandler(IDbContextProvider<TenantDbContextApp> dbContextProvider)
    {
        _dbContextProvider = dbContextProvider;
    }

    public async Task HandleAsync(
        TenantOwnershipInvitationCreatedEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.RequestId == Guid.Empty ||
            eventData.TenantId == Guid.Empty ||
            eventData.InvitationId == Guid.Empty)
        {
            return;
        }

        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var request = await unitOfWorkDbContext.TenantOwnershipRequests
            .FirstOrDefaultAsync(
                x => x.Id == eventData.RequestId && x.TenantId == eventData.TenantId,
                cancellationToken);
        if (request == null)
        {
            return;
        }

        if (request.AcknowledgeIdentityInvitation(eventData.InvitationId, eventData.OccurredAtUtc))
        {
            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
