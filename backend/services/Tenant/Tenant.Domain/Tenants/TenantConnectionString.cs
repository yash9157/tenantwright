using Shared;
using Shared.AuditLogs.Auditing;
using Shared.Domain.Entities;
using Tenant.Domain.Shared.Constants;

namespace Tenant.Domain.Tenants;

public class TenantConnectionString : Entity
{
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Value { get; private set; }
    public string? ProtectedValue { get; private set; }
    public string? ProtectionKeyId { get; private set; }
    public int? ProtectionKeyVersion { get; private set; }

    /// <summary>
    /// Non-reversible fingerprint of the normalized database target (host, port, database), unique across every
    /// tenant and name so no two dedicated assignments can ever point at the same physical database. Never the
    /// connection string; unaffected by key rotation or credential changes.
    /// </summary>
    public string? TargetFingerprint { get; private set; }

    protected TenantConnectionString()
    {
    }

    public TenantConnectionString(Guid tenantId, string name, string value)
    {
        TenantId = tenantId;
        Name = Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: TenantConsts.MaxConnectionStringNameLength);
        SetValue(value);
    }

    public TenantConnectionString(
        Guid tenantId,
        string name,
        string protectedValue,
        string keyId,
        int keyVersion,
        string targetFingerprint)
    {
        TenantId = tenantId;
        Name = Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: TenantConsts.MaxConnectionStringNameLength);
        SetProtectedValue(protectedValue, keyId, keyVersion);
        SetTargetFingerprint(targetFingerprint);
    }

    public void SetTargetFingerprint(string targetFingerprint)
    {
        TargetFingerprint = Check.NotNullOrWhiteSpace(
            targetFingerprint,
            nameof(targetFingerprint),
            maxLength: TenantConsts.MaxConnectionTargetFingerprintLength);
    }

    /// <summary>
    /// Marks the target fingerprint unknown. Only for assignments that already resolve to the same database target as
    /// another one (which the unique fingerprint index cannot represent); duplicate checks then compare them on the
    /// decrypted target instead.
    /// </summary>
    public void ClearTargetFingerprint()
    {
        TargetFingerprint = null;
    }

    public void SetValue(string value)
    {
        Value = Check.NotNullOrWhiteSpace(value, nameof(value));
        ProtectedValue = null;
        ProtectionKeyId = null;
        ProtectionKeyVersion = null;
    }

    public void SetProtectedValue(string protectedValue, string keyId, int keyVersion)
    {
        ProtectedValue = Check.NotNullOrWhiteSpace(protectedValue, nameof(protectedValue));
        ProtectionKeyId = Check.NotNullOrWhiteSpace(keyId, nameof(keyId), maxLength: 128);
        ProtectionKeyVersion = Math.Max(1, keyVersion);
        Value = null;
    }

    public string? GetStoredValue()
    {
        return ProtectedValue ?? Value;
    }

    public override object?[] GetKeys()
    {
        return new object?[] { TenantId, Name };
    }
}
