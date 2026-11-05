namespace Notifications.Application.Contracts.Channels;

public interface IPushNotificationSender
{
    Task<NotificationProviderSendResult> SendAsync(
        string deviceToken,
        string title,
        string body,
        CancellationToken cancellationToken = default);
}
