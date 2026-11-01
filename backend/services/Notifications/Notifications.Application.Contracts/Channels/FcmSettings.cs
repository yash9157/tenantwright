namespace Notifications.Application.Contracts.Channels;

/// <summary>Non-secret FCM settings stored as SettingsJson on a Channel=Push, ProviderType=Fcm config.</summary>
public sealed class FcmSettings
{
    public string ProjectId { get; set; } = "";
}

/// <summary>
/// Secret material for an FCM config: the Google service-account credential used to mint a
/// short-lived OAuth2 access token per send. Serialized together and encrypted as one SecretEnvelope.
/// </summary>
public sealed class FcmSecrets
{
    public string? ClientEmail { get; set; }
    public string? PrivateKey { get; set; }
}
