namespace Notifications.Application.Contracts.Channels;

public interface IWhatsAppNotificationSender
{
    Task<NotificationProviderSendResult> SendAsync(
        string phoneNumber,
        string templateName,
        string message,
        CancellationToken cancellationToken = default);
}
