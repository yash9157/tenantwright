using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.ProviderConfig;

/// <summary>
/// Resolves the single active provider configuration a channel send should use for a tenant:
/// the tenant's own active+default override if one exists, otherwise the host/Super Admin
/// default. Never falls back across unrelated tenants.
/// </summary>
public interface INotificationProviderConfigResolver
{
    Task<ResolvedNotificationProviderConfig?> ResolveAsync(
        Guid? tenantId,
        NotificationChannel channel,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up which provider configuration produced a previously sent delivery, by the provider's
    /// own message id. Used by anonymous provider webhooks (e.g. Twilio status callbacks) to resolve
    /// the correct secret to validate the callback's signature against — deliberately bypasses tenant
    /// ownership checks, since the caller has no tenant context and the message id is unguessable.
    /// Returns null when no delivery or config can be resolved.
    /// </summary>
    Task<ResolvedNotificationProviderConfig?> ResolveByProviderMessageIdAsync(
        string providerMessageId,
        NotificationChannel channel,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves a provider configuration by its public callback identifier for a signed webhook.</summary>
    Task<ResolvedNotificationProviderConfig?> ResolveByConfigIdAsync(
        Guid configId,
        NotificationChannel channel,
        CancellationToken cancellationToken = default);
}

public sealed class ResolvedNotificationProviderConfig
{
    public required Guid ConfigId { get; init; }

    public required Guid? ConfigTenantId { get; init; }

    public required NotificationChannel Channel { get; init; }

    public required string ProviderType { get; init; }

    /// <summary>Non-secret, provider-specific settings as raw JSON; deserialize with the adapter's own settings type.</summary>
    public required string SettingsJson { get; init; }

    /// <summary>Decrypted secret plaintext, or null when the config has no secret configured yet.</summary>
    public string? Secret { get; init; }
}
