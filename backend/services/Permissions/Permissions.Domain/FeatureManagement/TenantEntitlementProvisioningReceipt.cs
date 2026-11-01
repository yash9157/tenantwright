using Shared.AuditLogs.Auditing;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;
using Permissions.Domain.Shared.Constants;

namespace Permissions.Domain.FeatureManagement;

[Audited]
public sealed class TenantEntitlementProvisioningReceipt : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string SourceService { get; private set; } = default!;
    public Guid SourceAggregateId { get; private set; }
    public long SourceRevision { get; private set; }
    public DateTimeOffset EffectiveAtUtc { get; private set; }
    public string ValuesChecksum { get; private set; } = default!;
    public long ProviderStamp { get; private set; }
    public DateTimeOffset AppliedAtUtc { get; private set; }

    private TenantEntitlementProvisioningReceipt()
    {
    }

    public TenantEntitlementProvisioningReceipt(
        Guid id,
        Guid tenantId,
        string sourceService,
        Guid sourceAggregateId,
        long sourceRevision,
        DateTimeOffset effectiveAtUtc,
        string valuesChecksum,
        long providerStamp,
        DateTimeOffset appliedAtUtc)
        : base(id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Receipt id is required.", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(sourceService)) throw new ArgumentException("Source service is required.", nameof(sourceService));
        if (sourceService.Trim().Length > FeatureValueConsts.MaxProviderKeyLength)
            throw new ArgumentException("Source service is too long.", nameof(sourceService));
        if (sourceAggregateId == Guid.Empty) throw new ArgumentException("Source aggregate is required.", nameof(sourceAggregateId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRevision);
        if (effectiveAtUtc == default) throw new ArgumentException("Effective time is required.", nameof(effectiveAtUtc));
        if (string.IsNullOrWhiteSpace(valuesChecksum) || valuesChecksum.Trim().Length != 64)
            throw new ArgumentException("A SHA-256 values checksum is required.", nameof(valuesChecksum));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(providerStamp);
        if (appliedAtUtc == default) throw new ArgumentException("Applied time is required.", nameof(appliedAtUtc));

        Id = id;
        TenantId = tenantId;
        SourceService = sourceService.Trim();
        SourceAggregateId = sourceAggregateId;
        SourceRevision = sourceRevision;
        EffectiveAtUtc = effectiveAtUtc;
        ValuesChecksum = valuesChecksum.Trim();
        ProviderStamp = providerStamp;
        AppliedAtUtc = appliedAtUtc;
    }
}
