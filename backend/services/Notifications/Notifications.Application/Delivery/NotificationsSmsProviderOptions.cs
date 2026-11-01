namespace Notifications.Application.Delivery;

/// <summary>
/// Real SMS provider credentials/config, bound from <c>Notifications:Sms</c>. Used only when
/// <c>Notifications:Providers:Sms</c> is <c>Live</c>. The auth token is a secret and must never be
/// logged.
/// </summary>
public sealed class NotificationsSmsProviderOptions
{
    /// <summary>Vendor selecting the adapter (e.g. "Twilio").</summary>
    public string Provider { get; set; } = "Twilio";

    public string AccountSid { get; set; } = string.Empty;

    public string AuthToken { get; set; } = string.Empty;

    public string FromNumber { get; set; } = string.Empty;

    /// <summary>
    /// Public base URL Twilio calls back on (scheme + host), used to reconstruct the exact signed URL
    /// for signature validation when the service sits behind a gateway/proxy. When empty, the incoming
    /// request URL is used.
    /// </summary>
    public string? WebhookBaseUrl { get; set; }
}
