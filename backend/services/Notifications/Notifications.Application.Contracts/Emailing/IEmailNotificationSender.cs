using Notifications.Application.Contracts.Channels;

namespace Notifications.Application.Contracts.Emailing;

public interface IEmailNotificationSender
{
    Task<NotificationProviderSendResult> SendAsync(
        string to,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default);
}
