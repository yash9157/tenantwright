using Notifications.Application.Contracts.ProviderConfig;

namespace Notifications.Application.Contracts.Channels;

/// <summary>Outcome of a real WhatsApp provider send.</summary>
public sealed class WhatsAppProviderSendResult
{
    private WhatsAppProviderSendResult(bool isSuccess, string? providerMessageId, string? errorCode)
    {
        IsSuccess = isSuccess;
        ProviderMessageId = providerMessageId;
        ErrorCode = errorCode;
    }

    public bool IsSuccess { get; }

    public string? ProviderMessageId { get; }

    public string? ErrorCode { get; }

    public static WhatsAppProviderSendResult Success(string? providerMessageId) => new(true, providerMessageId, null);

    public static WhatsAppProviderSendResult Failed(string errorCode) => new(false, null, errorCode);
}

/// <summary>
/// A concrete WhatsApp vendor integration (e.g. Meta Cloud API). Selected by <c>ProviderName</c>
/// matching the resolved provider configuration's <c>ProviderType</c>. Sends only approved-template
/// messages — free-form messages outside an approved template must fail safely, never silently
/// degrade to a different send shape.
/// </summary>
public interface IWhatsAppProviderAdapter
{
    string ProviderName { get; }

    Task<WhatsAppProviderSendResult> SendAsync(
        string toPhoneNumber,
        string templateName,
        string bodyText,
        ResolvedNotificationProviderConfig config,
        CancellationToken cancellationToken = default);
}
