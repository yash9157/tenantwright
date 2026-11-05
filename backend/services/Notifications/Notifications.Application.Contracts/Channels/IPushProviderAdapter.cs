using Notifications.Application.Contracts.ProviderConfig;

namespace Notifications.Application.Contracts.Channels;

/// <summary>
/// A concrete push vendor integration (e.g. FCM). Selected by <c>ProviderName</c> matching the
/// resolved provider configuration's <c>ProviderType</c>.
/// </summary>
public interface IPushProviderAdapter
{
    string ProviderName { get; }

    Task<NotificationProviderSendResult> SendAsync(
        string deviceToken,
        string title,
        string body,
        ResolvedNotificationProviderConfig config,
        CancellationToken cancellationToken = default);
}
