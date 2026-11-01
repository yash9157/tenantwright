using Notifications.Application.Contracts.ProviderConfig;

namespace Notifications.Application.Contracts.Channels;

/// <summary>Outcome of a real SMS provider send.</summary>
public sealed class SmsProviderSendResult
{
    private SmsProviderSendResult(bool isSuccess, string? providerMessageId, string? errorCode)
    {
        IsSuccess = isSuccess;
        ProviderMessageId = providerMessageId;
        ErrorCode = errorCode;
    }

    public bool IsSuccess { get; }

    public string? ProviderMessageId { get; }

    public string? ErrorCode { get; }

    public static SmsProviderSendResult Success(string? providerMessageId) => new(true, providerMessageId, null);

    public static SmsProviderSendResult Failed(string errorCode) => new(false, null, errorCode);
}

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

    Task<SmsProviderSendResult> SendAsync(
        string toPhoneNumber,
        string message,
        ResolvedNotificationProviderConfig config,
        CancellationToken cancellationToken = default);
}
