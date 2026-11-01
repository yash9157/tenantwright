using Shared.Domain.Entities;
using Shared.AuditLogs.Auditing;
using Permissions.Domain.Shared.Constants;

namespace Permissions.Domain.FeatureManagement;

[Audited]
public class FeatureValue : Entity<Guid>
{
    public Guid? TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string Value { get; private set; } = default!;
    public string ProviderName { get; private set; } = default!;
    public string? ProviderKey { get; private set; }
    public string? ProvisioningSource { get; private set; }
    public Guid? ProvisioningSourceAggregateId { get; private set; }
    public long? ProvisioningSourceRevision { get; private set; }
    public DateTimeOffset? ProvisioningEffectiveAtUtc { get; private set; }

    protected FeatureValue()
    {
    }

    public FeatureValue(Guid id, string name, string value, string providerName, string? providerKey, Guid? tenantId = null)
        : base(id)
    {
        TenantId = tenantId;
        Name = name.Trim();
        Value = value.Trim();
        ProviderName = providerName.Trim();
        ProviderKey = string.IsNullOrWhiteSpace(providerKey) ? null : providerKey.Trim();
    }

    public void SetValue(string value)
    {
        if (ProvisioningSource is not null)
            throw new InvalidOperationException("Machine-managed feature values require their provisioning source.");
        Value = value.Trim();
    }

    public bool IsManagedBy(string source) =>
        string.Equals(ProvisioningSource, source, StringComparison.Ordinal);

    public static FeatureValue CreateProvisioned(
        Guid id,
        string name,
        string value,
        string providerName,
        string? providerKey,
        Guid tenantId,
        string source,
        Guid sourceAggregateId,
        long sourceRevision,
        DateTimeOffset effectiveAtUtc)
    {
        var featureValue = new FeatureValue(id, name, value, providerName, providerKey, tenantId);
        featureValue.SetProvisioningMetadata(source, sourceAggregateId, sourceRevision, effectiveAtUtc);
        return featureValue;
    }

    public void ApplyProvisionedValue(
        string value,
        string source,
        Guid sourceAggregateId,
        long sourceRevision,
        DateTimeOffset effectiveAtUtc)
    {
        if (ProvisioningSource is null)
            throw new InvalidOperationException("Operator-managed feature values cannot be reclassified by provisioning.");
        if (!IsManagedBy(source))
            throw new InvalidOperationException("The feature value is managed by a different provisioning source.");

        SetProvisioningMetadata(source, sourceAggregateId, sourceRevision, effectiveAtUtc);
        Value = value.Trim();
    }

    private void SetProvisioningMetadata(
        string source,
        Guid sourceAggregateId,
        long sourceRevision,
        DateTimeOffset effectiveAtUtc)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Provisioning source is required.", nameof(source));
        if (source.Trim().Length > FeatureValueConsts.MaxProviderKeyLength)
            throw new ArgumentException("Provisioning source is too long.", nameof(source));
        if (sourceAggregateId == Guid.Empty) throw new ArgumentException("Provisioning source aggregate is required.", nameof(sourceAggregateId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRevision);
        if (effectiveAtUtc == default) throw new ArgumentException("Provisioning effective time is required.", nameof(effectiveAtUtc));

        ProvisioningSource = source.Trim();
        ProvisioningSourceAggregateId = sourceAggregateId;
        ProvisioningSourceRevision = sourceRevision;
        ProvisioningEffectiveAtUtc = effectiveAtUtc;
    }
}
