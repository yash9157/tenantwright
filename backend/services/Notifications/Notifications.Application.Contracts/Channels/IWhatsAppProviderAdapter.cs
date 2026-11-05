using Notifications.Application.Contracts.ProviderConfig;

namespace Notifications.Application.Contracts.Channels;

/// <summary>
/// A concrete WhatsApp vendor integration (e.g. Meta Cloud API). Selected by <c>ProviderName</c>
/// matching the resolved provider configuration's <c>ProviderType</c>. Sends only approved-template
/// messages — free-form messages outside an approved template must fail safely, never silently
/// degrade to a different send shape.
/// </summary>
public interface IWhatsAppProviderAdapter
{
    string ProviderName { get; }

    Task<NotificationProviderSendResult> SendAsync(
        string toPhoneNumber,
        string templateName,
        string bodyText,
        ResolvedNotificationProviderConfig config,
        CancellationToken cancellationToken = default);
}
