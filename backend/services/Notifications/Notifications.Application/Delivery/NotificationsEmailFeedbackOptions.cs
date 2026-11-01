namespace Notifications.Application.Delivery;

/// <summary>
/// Host-only configuration for the email delivery-feedback webhook. The signing secret is
/// write-only (never returned by any read/diagnostics API). Disabled by default until a
/// transactional-email provider is selected and configured.
/// </summary>
public sealed class NotificationsEmailFeedbackOptions
{
    public const string SectionName = "Notifications:EmailFeedback";

    /// <summary>When false, the webhook endpoint rejects all requests (feature off).</summary>
    public bool Enabled { get; set; }

    /// <summary>The selected provider name; must match the webhook route segment when set.</summary>
    public string? Provider { get; set; }

    /// <summary>HMAC signing secret shared with the provider. Host-only, write-only.</summary>
    public string? SigningSecret { get; set; }

    /// <summary>Header carrying the provider's request signature (hex HMAC-SHA256).</summary>
    public string SignatureHeader { get; set; } = "X-Signature";

    /// <summary>Optional header carrying the signed timestamp (seconds or ISO-8601) for replay defense.</summary>
    public string TimestampHeader { get; set; } = "X-Timestamp";

    /// <summary>Maximum accepted age of a signed timestamp, in seconds.</summary>
    public int TimestampToleranceSeconds { get; set; } = 300;

    /// <summary>Maximum accepted raw body size in bytes.</summary>
    public int MaxBodyBytes { get; set; } = 262144;

    /// <summary>Maximum number of events accepted in a single callback.</summary>
    public int MaxEvents { get; set; } = 100;
}
