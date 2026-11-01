using Notifications.Application.Contracts.ProviderConfig;

namespace Notifications.Application.Contracts.Channels;

/// <summary>Outcome of a real push provider send.</summary>
public sealed class PushProviderSendResult
{
    private PushProviderSendResult(bool isSuccess, string? providerMessageId, string? errorCode)
    {
        IsSuccess = isSuccess;
        ProviderMessageId = providerMessageId;
        ErrorCode = errorCode;
    }

    public bool IsSuccess { get; }

    public string? ProviderMessageId { get; }

    public string? ErrorCode { get; }

    public static PushProviderSendResult Success(string? providerMessageId) => new(true, providerMessageId, null);

    public static PushProviderSendResult Failed(string errorCode) => new(false, null, errorCode);
}

/// <summary>
/// A concrete push vendor integration (e.g. FCM). Selected by <c>ProviderName</c> matching the
/// resolved provider configuration's <c>ProviderType</c>.
/// </summary>
public interface IPushProviderAdapter
{
    string ProviderName { get; }

    Task<PushProviderSendResult> SendAsync(
        string deviceToken,
        string title,
        string body,
        ResolvedNotificationProviderConfig config,
        CancellationToken cancellationToken = default);
}
