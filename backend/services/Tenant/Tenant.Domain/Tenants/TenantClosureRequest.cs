using Shared.Domain.Entities.Auditing;
using Tenant.Domain.Shared.Enums;

namespace Tenant.Domain.Tenants;

public sealed class TenantClosureRequest : FullAuditedAggregateRoot<Guid>
{
    public Guid TenantId { get; private set; }
    public Guid RequestedByUserId { get; private set; }
    public string ReasonCode { get; private set; } = string.Empty;
    public string? Note { get; private set; }
    public TenantClosureStatus Status { get; private set; }
    public Guid? ApprovedByUserId { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public DateTime? RestoredAtUtc { get; private set; }
    public Guid? RejectedByUserId { get; private set; }
    public DateTime? RejectedAtUtc { get; private set; }
    public string? DecisionReasonCode { get; private set; }
    public string? DecisionNote { get; private set; }

    private TenantClosureRequest() { }
    public TenantClosureRequest(Guid id, Guid tenantId, Guid requestedByUserId, string reasonCode, string? note) : base(id)
    {
        TenantId = tenantId; RequestedByUserId = requestedByUserId; ReasonCode = reasonCode; Note = note; Status = TenantClosureStatus.Requested;
    }
    public void Cancel() { if (Status == TenantClosureStatus.Requested) Status = TenantClosureStatus.Canceled; }
    public void Approve(Guid approvedByUserId, DateTime utcNow) { if (Status != TenantClosureStatus.Requested) throw new InvalidOperationException("Only a requested closure can be approved."); ApprovedByUserId = approvedByUserId; ApprovedAtUtc = utcNow; Status = TenantClosureStatus.Approved; }
    public void Reject(Guid rejectedByUserId, DateTime utcNow, string reasonCode, string? note)
    {
        if (Status != TenantClosureStatus.Requested) throw new InvalidOperationException("Only a requested closure can be rejected.");
        if (rejectedByUserId == Guid.Empty) throw new ArgumentException("Rejecting user is required.", nameof(rejectedByUserId));
        if (string.IsNullOrWhiteSpace(reasonCode)) throw new ArgumentException("Decision reason is required.", nameof(reasonCode));
        RejectedByUserId = rejectedByUserId;
        RejectedAtUtc = utcNow;
        DecisionReasonCode = reasonCode.Trim();
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Status = TenantClosureStatus.Rejected;
    }
    public void Close(DateTime utcNow) { if (Status != TenantClosureStatus.Approved) throw new InvalidOperationException("Only an approved closure can be closed."); ClosedAtUtc = utcNow; Status = TenantClosureStatus.Closed; }
    public void Restore(DateTime utcNow) { if (Status != TenantClosureStatus.Closed) throw new InvalidOperationException("Only a closed tenant can be restored."); RestoredAtUtc = utcNow; Status = TenantClosureStatus.Restored; }
}
