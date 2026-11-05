namespace Notifications.Application.Contracts.Channels;

public interface ISmsNotificationSender
{
    Task<NotificationProviderSendResult> SendAsync(
        string phoneNumber,
        string message,
        CancellationToken cancellationToken = default);
}
