using Microsoft.AspNetCore.SignalR;
using Notifications.Application.Contracts.Realtime;
using Notifications.Hubs;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Realtime;

[DisableConventionalRegistration]
public sealed class SignalRNotificationsRealtimePublisher : INotificationsRealtimePublisher, ITransientDependency
{
    private readonly IHubContext<NotificationsHub> _hubContext;

    public SignalRNotificationsRealtimePublisher(IHubContext<NotificationsHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishAppNotificationAsync(AppNotificationSignalDto input, CancellationToken cancellationToken = default)
    {
        var group = NotificationsRealtimeGroupNames.UserGroup(input.TenantId, input.UserId);
        return _hubContext.Clients.Group(group).SendAsync("notificationReceived", input, cancellationToken);
    }

    public Task PublishSessionRevokedAsync(SessionRevokedSignalDto input, CancellationToken cancellationToken = default)
    {
        var group = NotificationsRealtimeGroupNames.UserGroup(input.TenantId, input.UserId);
        return _hubContext.Clients.Group(group).SendAsync("sessionRevoked", input, cancellationToken);
    }
}
