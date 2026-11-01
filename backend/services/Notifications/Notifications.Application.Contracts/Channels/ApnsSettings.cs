namespace Notifications.Application.Contracts.Channels;

/// <summary>Non-secret APNs configuration stored with a Push provider config.</summary>
public sealed class ApnsSettings
{
    public string TeamId { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string BundleId { get; set; } = string.Empty;
    public bool UseSandbox { get; set; }
}

/// <summary>Write-only APNs signing material, encrypted inside the provider-secret envelope.</summary>
public sealed class ApnsSecrets
{
    public string? PrivateKey { get; set; }
}
