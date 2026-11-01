using Shared.Domain.Entities.Auditing;
using Tenant.Domain.Shared.Enums;

namespace Tenant.Domain.Tenants;

public sealed class TenantLifecycleTransition : CreationAuditedAggregateRoot<Guid>
{
    public Guid TenantId { get; private set; }
    public TenantLifecycleStatus PreviousStatus { get; private set; }
    public TenantLifecycleStatus CurrentStatus { get; private set; }
    public string ReasonCode { get; private set; } = default!;
    public string? Note { get; private set; }

    private TenantLifecycleTransition()
    {
    }

    public TenantLifecycleTransition(
        Guid id,
        Guid tenantId,
        TenantLifecycleStatus previousStatus,
        TenantLifecycleStatus currentStatus,
        string reasonCode,
        string? note)
        : base(id)
    {
        TenantId = tenantId;
        PreviousStatus = previousStatus;
        CurrentStatus = currentStatus;
        ReasonCode = reasonCode;
        Note = note;
    }
}
