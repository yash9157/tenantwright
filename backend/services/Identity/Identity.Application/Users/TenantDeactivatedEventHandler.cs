using Identity.Application.Contracts.Users;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace Identity.Application.Users;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<TenantDeactivatedEto>),
    IncludeSelf = true)]
public sealed class TenantDeactivatedEventHandler
    : ITenantWrightMessageHandler<TenantDeactivatedEto>,
        ITransientDependency
{
    private readonly IIdentitySessionRevocationService _sessionRevocationService;

    public TenantDeactivatedEventHandler(IIdentitySessionRevocationService sessionRevocationService)
    {
        _sessionRevocationService = sessionRevocationService;
    }

    public Task HandleAsync(
        TenantDeactivatedEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.TenantId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        return _sessionRevocationService.RevokeAllByTenantAsync(
            eventData.TenantId,
            action: "TenantDeactivated",
            revocationSource: "TenantDeactivated",
            revokedByUserId: null,
            revocationReason: $"Tenant {eventData.TenantName ?? eventData.TenantId.ToString("D")} deactivated",
            cancellationToken: cancellationToken);
    }
}
