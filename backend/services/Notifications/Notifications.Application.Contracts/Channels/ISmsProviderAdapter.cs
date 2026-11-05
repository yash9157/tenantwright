using Notifications.Application.Contracts.ProviderConfig;

namespace Notifications.Application.Contracts.Channels;

/// <summary>
/// A concrete SMS vendor integration (e.g. Twilio). Selected by <c>ProviderName</c> matching the
/// resolved provider configuration's <c>ProviderType</c> when the SMS provider mode is Live. Each
/// adapter deserializes its own non-secret settings from <see cref="ResolvedNotificationProviderConfig.SettingsJson"/>
/// and reads its own secret shape from <see cref="ResolvedNotificationProviderConfig.Secret"/> — the
/// scheduler/sender never branches on vendor-specific credential fields.
/// </summary>
public interface ISmsProviderAdapter
{
    string ProviderName { get; }

    Task<NotificationProviderSendResult> SendAsync(
        string toPhoneNumber,
        string message,
        ResolvedNotificationProviderConfig config,
        CancellationToken cancellationToken = default);
}
