namespace Notifications.Application.Contracts.Templates;

public interface ITemplateStore
{
    Task<StoredNotificationTemplateContent?> GetAsync(string templateName, CancellationToken cancellationToken = default);
}
