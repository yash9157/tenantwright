namespace Notifications.Application.Contracts.Channels;

/// <summary>
/// Non-secret Twilio settings stored as SettingsJson on a Channel=Sms, ProviderType=Twilio config.
/// Lives in Application.Contracts because it is deserialized both by the Application-layer adapter
/// and by the Host's anonymous Twilio webhook controller (to rebuild the signed callback URL).
/// </summary>
public sealed class TwilioSmsSettings
{
    public string AccountSid { get; set; } = "";
    public string FromNumber { get; set; } = "";
    public string? MessagingServiceSid { get; set; }
    public string? WebhookBaseUrl { get; set; }
}
