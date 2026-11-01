namespace Notifications.Application.ProviderConfig;

/// <summary>
/// Cached lookup result for the active+default provider configuration of one (tenant scope,
/// channel) pair. <see cref="Found"/> false is cached too, so an absent tenant override does
/// not re-query the database on every send.
/// </summary>
public sealed class NotificationProviderConfigCacheItem
{
    public bool Found { get; set; }

    public Guid ConfigId { get; set; }

    public Guid? ConfigTenantId { get; set; }

    public string ProviderType { get; set; } = string.Empty;

    public string SettingsJson { get; set; } = "{}";

    /// <summary>Still encrypted; decrypted lazily by the resolver at read time, never cached in plaintext.</summary>
    public string? SecretEnvelope { get; set; }
}
