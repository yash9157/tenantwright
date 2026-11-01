namespace Notifications.Application.Contracts.Realtime;

public interface INotificationsRealtimePublisher
{
    Task PublishAppNotificationAsync(AppNotificationSignalDto input, CancellationToken cancellationToken = default);
    Task PublishSessionRevokedAsync(SessionRevokedSignalDto input, CancellationToken cancellationToken = default);
}
