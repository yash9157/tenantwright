using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.ProviderConfig;

/// <summary>
/// One scoped configuration for a channel's outbound provider (Email/Sms/Push/WhatsApp).
/// TenantId null means a Super Admin host default available to every tenant; a non-null
/// TenantId means a Tenant Admin configuration owned by and visible only to that tenant.
/// Exactly one active+default row may exist per (TenantId, Channel).
/// </summary>
public class NotificationProviderConfig : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public string ScopeKey { get; private set; } = "host";

    public NotificationChannel Channel { get; private set; }

    public string ProviderType { get; private set; } = default!;

    public string Name { get; private set; } = default!;

    public bool IsActive { get; private set; }

    public bool IsDefault { get; private set; }

    public int Priority { get; private set; }

    /// <summary>Non-secret, provider-specific settings serialized as JSON (jsonb).</summary>
    public string SettingsJson { get; private set; } = "{}";

    /// <summary>AES-GCM encrypted-secret envelope (JSON). Null when the config has no secret yet.</summary>
    public string? SecretEnvelope { get; private set; }

    /// <summary>Non-reversible masked fingerprint of the current secret, for display only.</summary>
    public string? SecretFingerprint { get; private set; }

    /// <summary>Non-secret identifier of the protection key used by the current envelope.</summary>
    public string? SecretProtectionKeyId { get; private set; }

    /// <summary>
    /// Who owns SMS/WhatsApp regulatory compliance (sender registration, template approval,
    /// consent/opt-out evidence) for this configuration: the tenant for a tenant-owned
    /// configuration, or the host/platform team for the shared host-default configuration.
    /// Free-text contact/team reference, audited, not access-control.
    /// </summary>
    public string? ComplianceContact { get; private set; }

    public DateTime? LastTestedAtUtc { get; private set; }

    public string? LastTestResult { get; private set; }

    public NotificationProviderVerificationState VerificationState { get; private set; } = NotificationProviderVerificationState.Configured;
    public DateTime? LiveVerifiedAtUtc { get; private set; }
    public string? LiveVerificationReference { get; private set; }

    protected NotificationProviderConfig()
    {
    }

    public NotificationProviderConfig(
        Guid id,
        Guid? tenantId,
        NotificationChannel channel,
        string providerType,
        string name,
        bool isActive,
        bool isDefault,
        int priority,
        string settingsJson,
        string? complianceContact)
        : base(id)
    {
        TenantId = tenantId;
        ScopeKey = tenantId?.ToString("D") ?? "host";
        Channel = RequireConfigurableChannel(channel);
        ProviderType = global::Shared.Check.NotNullOrWhiteSpace(providerType, nameof(providerType), maxLength: 64);
        Name = global::Shared.Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: 128);
        IsActive = isActive;
        IsDefault = isDefault;
        Priority = priority;
        SettingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
        ComplianceContact = NormalizeOptional(complianceContact, 256);
    }

    public void UpdateSettings(
        string providerType,
        string name,
        bool isActive,
        int priority,
        string settingsJson,
        string? complianceContact)
    {
        ProviderType = global::Shared.Check.NotNullOrWhiteSpace(providerType, nameof(providerType), maxLength: 64);
        Name = global::Shared.Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: 128);
        IsActive = isActive;
        Priority = priority;
        SettingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
        ComplianceContact = NormalizeOptional(complianceContact, 256);
    }

    public void SetSecret(string encryptedEnvelope, string? fingerprint, string? protectionKeyId = null)
    {
        SecretEnvelope = global::Shared.Check.NotNullOrWhiteSpace(encryptedEnvelope, nameof(encryptedEnvelope));
        SecretFingerprint = NormalizeOptional(fingerprint, 32);
        SecretProtectionKeyId = NormalizeOptional(protectionKeyId, 128);
    }

    public void RecordTestResult(DateTime testedAtUtc, string result)
    {
        LastTestedAtUtc = testedAtUtc;
        LastTestResult = global::Shared.Check.NotNullOrWhiteSpace(result, nameof(result), maxLength: 64);
        VerificationState = string.Equals(LastTestResult, "Success", StringComparison.OrdinalIgnoreCase)
            ? NotificationProviderVerificationState.TestSendPassed
            : NotificationProviderVerificationState.VerificationFailed;
        if (VerificationState != NotificationProviderVerificationState.LiveVerified)
        {
            LiveVerifiedAtUtc = null;
            LiveVerificationReference = null;
        }
    }

    public void RecordLiveVerification(DateTime verifiedAtUtc, string evidenceReference)
    {
        if (VerificationState != NotificationProviderVerificationState.TestSendPassed)
        {
            throw new InvalidOperationException("A successful configuration-specific test send is required before live verification.");
        }

        VerificationState = NotificationProviderVerificationState.LiveVerified;
        LiveVerifiedAtUtc = verifiedAtUtc.Kind == DateTimeKind.Utc ? verifiedAtUtc : verifiedAtUtc.ToUniversalTime();
        LiveVerificationReference = NormalizeOptional(evidenceReference, 128)
            ?? throw new ArgumentException("Verification evidence reference is required.", nameof(evidenceReference));
    }

    public void MarkAsDefault()
    {
        IsDefault = true;
    }

    public void ClearDefault()
    {
        IsDefault = false;
    }

    private static NotificationChannel RequireConfigurableChannel(NotificationChannel channel)
    {
        if (channel is not (NotificationChannel.Email or NotificationChannel.Sms or NotificationChannel.Push or NotificationChannel.WhatsApp))
        {
            throw new ArgumentException($"Channel '{channel}' does not use a provider configuration.", nameof(channel));
        }

        return channel;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (normalized is { Length: > 0 } && normalized.Length > maxLength)
        {
            throw new ArgumentException($"Value length must be equal to or lower than {maxLength}!");
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
