using Notifications.Application.Contracts.Realtime;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Realtime;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<IdentitySessionRevokedTenantEto>),
    typeof(ITenantWrightMessageHandler<IdentitySessionRevokedHostEto>),
    IncludeSelf = true)]
public sealed class IdentitySessionRevokedEventHandler
    : ITenantWrightMessageHandler<IdentitySessionRevokedTenantEto>,
        ITenantWrightMessageHandler<IdentitySessionRevokedHostEto>,
        ITransientDependency
{
    private readonly INotificationsRealtimePublisher _publisher;

    public IdentitySessionRevokedEventHandler(INotificationsRealtimePublisher publisher)
    {
        _publisher = publisher;
    }

    public Task HandleAsync(
        IdentitySessionRevokedTenantEto eventData,
        CancellationToken cancellationToken = default)
    {
        if (!eventData.TenantId.HasValue || eventData.TenantId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Tenant session-revocation events require a non-empty tenant id.");
        }

        return PublishAsync(
            eventData.UserId,
            eventData.SessionId,
            eventData.Action,
            eventData.OccurredAtUtc,
            eventData.TenantId.Value,
            cancellationToken);
    }

    public Task HandleAsync(
        IdentitySessionRevokedHostEto eventData,
        CancellationToken cancellationToken = default)
        => PublishAsync(
            eventData.UserId,
            eventData.SessionId,
            eventData.Action,
            eventData.OccurredAtUtc,
            tenantId: null,
            cancellationToken);

    private Task PublishAsync(
        Guid userId,
        string sessionId,
        string action,
        DateTime occurredAtUtc,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        return _publisher.PublishSessionRevokedAsync(new SessionRevokedSignalDto
        {
            TenantId = tenantId,
            UserId = userId.ToString("D"),
            SessionId = sessionId,
            Action = action,
            OccurredAtUtc = occurredAtUtc
        }, cancellationToken);
    }
}
