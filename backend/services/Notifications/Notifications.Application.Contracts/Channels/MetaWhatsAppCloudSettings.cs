namespace Notifications.Application.Contracts.Channels;

/// <summary>
/// Non-secret Meta WhatsApp Cloud API settings stored as SettingsJson on a Channel=WhatsApp,
/// ProviderType=MetaWhatsAppCloud config. <see cref="ApprovedTemplateNames"/> is enforced fail-closed:
/// a send for a template not in this list is rejected rather than falling back to a free-form message.
/// </summary>
public sealed class MetaWhatsAppCloudSettings
{
    public string PhoneNumberId { get; set; } = "";
    public string? BusinessAccountId { get; set; }
    public List<string> ApprovedTemplateNames { get; set; } = [];
    public string TemplateLanguageCode { get; set; } = "en_US";

    /// <summary>
    /// Verification token Meta calls the webhook GET handshake with. Only the Super Admin host
    /// default config's value is checked — Meta webhook subscription is an App-level (host-level)
    /// concept shared by every tenant's phone number, not a per-tenant one.
    /// </summary>
    public string? WebhookVerifyToken { get; set; }
}

/// <summary>
/// Secret material for a Meta WhatsApp Cloud config, serialized together and encrypted as one
/// SecretEnvelope. AccessToken is per-config (may differ per tenant); AppSecret is only meaningful
/// on the host default config, since it signs webhook callbacks for the whole Meta App.
/// </summary>
public sealed class MetaWhatsAppCloudSecrets
{
    public string? AccessToken { get; set; }
    public string? AppSecret { get; set; }
}
