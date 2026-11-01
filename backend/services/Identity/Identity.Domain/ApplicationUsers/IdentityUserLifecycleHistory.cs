using Identity.Domain.Shared.ApplicationUsers;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;

namespace Identity.Domain.ApplicationUsers;

public sealed class IdentityUserLifecycleHistory : CreationAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public IdentityUserLifecycleStatus? PreviousStatus { get; private set; }
    public IdentityUserLifecycleStatus CurrentStatus { get; private set; }
    public DateTime EffectiveAtUtc { get; private set; }
    public string ReasonCode { get; private set; } = string.Empty;
    public string? Note { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public bool IsMigrationBackfill { get; private set; }

    private IdentityUserLifecycleHistory()
    {
    }

    public IdentityUserLifecycleHistory(
        Guid id,
        Guid? tenantId,
        Guid userId,
        IdentityUserLifecycleStatus? previousStatus,
        IdentityUserLifecycleStatus currentStatus,
        DateTime effectiveAtUtc,
        string reasonCode,
        string? note,
        Guid? actorUserId,
        string correlationId,
        bool isMigrationBackfill = false)
        : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        PreviousStatus = previousStatus;
        CurrentStatus = currentStatus;
        EffectiveAtUtc = effectiveAtUtc;
        ReasonCode = reasonCode;
        Note = note;
        ActorUserId = actorUserId;
        CorrelationId = correlationId;
        IsMigrationBackfill = isMigrationBackfill;
    }
}
