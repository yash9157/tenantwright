namespace Notifications.Application.Contracts.Emailing;

public interface IEmailNotificationSender
{
    Task<string?> SendAsync(
        string to,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default);
}
