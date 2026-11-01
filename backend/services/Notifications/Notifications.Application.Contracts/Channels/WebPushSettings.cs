namespace Notifications.Application.Contracts.Channels;

/// <summary>Non-secret VAPID configuration for a standards-based Web Push provider.</summary>
public sealed class WebPushSettings
{
    public string Subject { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
}

/// <summary>Write-only VAPID signing material encrypted in the provider-secret envelope.</summary>
public sealed class WebPushSecrets
{
    public string? PrivateKey { get; set; }
}

/// <summary>Protected representation of a browser PushSubscription. Never return this from an API.</summary>
public sealed class WebPushSubscriptionPayload
{
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
}
