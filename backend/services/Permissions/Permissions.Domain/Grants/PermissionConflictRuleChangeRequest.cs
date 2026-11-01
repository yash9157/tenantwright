using Shared;
using Shared.Domain;
using Shared.Domain.Entities;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.AuditLogs.Auditing;

namespace Permissions.Domain.Grants;

/// <summary>
/// A four-eyes request to create, update, or delete a separation-of-duty
/// <see cref="PermissionConflictRule"/>. Conflict-rule mutations are consequential (they can block
/// future grants), so they are never applied directly: a requester records one of these and a
/// <b>different</b> operator holding the dedicated decide authority approves or rejects it. The
/// requester may withdraw their own pending request. Approval applies the change transactionally.
/// </summary>
[Audited]
public class PermissionConflictRuleChangeRequest : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public PermissionConflictRuleChangeType ChangeType { get; private set; }

    /// <summary>The rule targeted by an <see cref="PermissionConflictRuleChangeType.Update"/> or
    /// <see cref="PermissionConflictRuleChangeType.Delete"/>; null for a create.</summary>
    public Guid? TargetRuleId { get; private set; }

    /// <summary>Expected version of the target rule, used to fail closed if the rule changed after the
    /// request was raised; null for a create.</summary>
    public long? ExpectedRuleVersion { get; private set; }

    /// <summary>Proposed first permission of the pair (create/update); the target rule's value for a delete.</summary>
    public string PermissionA { get; private set; } = default!;

    public string PermissionB { get; private set; } = default!;

    /// <summary>Proposed rule reason (create/update). Distinct from <see cref="RequestReason"/>.</summary>
    public string? RuleReason { get; private set; }

    /// <summary>Why this change was requested (audit/justification).</summary>
    public string? RequestReason { get; private set; }

    public string? TicketId { get; private set; }

    public Guid? RequestedByUserId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public PermissionConflictRuleChangeStatus Status { get; private set; } = PermissionConflictRuleChangeStatus.Pending;

    public Guid? DecidedByUserId { get; private set; }

    public string? DecisionReason { get; private set; }

    public DateTime? DecidedAtUtc { get; private set; }

    public long Version { get; private set; } = 1;

    protected PermissionConflictRuleChangeRequest()
    {
    }

    private PermissionConflictRuleChangeRequest(
        Guid id,
        Guid? tenantId,
        PermissionConflictRuleChangeType changeType,
        Guid? targetRuleId,
        long? expectedRuleVersion,
        string permissionA,
        string permissionB,
        string? ruleReason,
        string? requestReason,
        string? ticketId,
        Guid? requestedByUserId,
        DateTime createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        ChangeType = changeType;
        TargetRuleId = targetRuleId;
        ExpectedRuleVersion = expectedRuleVersion;
        PermissionA = Check.NotNullOrWhiteSpace(permissionA, nameof(permissionA), maxLength: PermissionsConsts.MaxPermissionNameLength);
        PermissionB = Check.NotNullOrWhiteSpace(permissionB, nameof(permissionB), maxLength: PermissionsConsts.MaxPermissionNameLength);
        RuleReason = Normalize(ruleReason, PermissionGrantConsts.MaxReasonLength);
        RequestReason = Normalize(requestReason, PermissionGrantConsts.MaxReasonLength);
        TicketId = Normalize(ticketId, PermissionGrantConsts.MaxTicketIdLength);
        RequestedByUserId = requestedByUserId;
        CreatedAtUtc = createdAtUtc;
        Status = PermissionConflictRuleChangeStatus.Pending;
        Version = 1;
    }

    public static PermissionConflictRuleChangeRequest ForCreate(
        Guid id, Guid? tenantId, string permissionA, string permissionB, string? ruleReason,
        string? requestReason, string? ticketId, Guid? requestedByUserId, DateTime createdAtUtc)
        => new(id, tenantId, PermissionConflictRuleChangeType.Create, targetRuleId: null, expectedRuleVersion: null,
            permissionA, permissionB, ruleReason, requestReason, ticketId, requestedByUserId, createdAtUtc);

    public static PermissionConflictRuleChangeRequest ForUpdate(
        Guid id, Guid? tenantId, Guid targetRuleId, long expectedRuleVersion, string permissionA, string permissionB,
        string? ruleReason, string? requestReason, string? ticketId, Guid? requestedByUserId, DateTime createdAtUtc)
        => new(id, tenantId, PermissionConflictRuleChangeType.Update, targetRuleId, expectedRuleVersion,
            permissionA, permissionB, ruleReason, requestReason, ticketId, requestedByUserId, createdAtUtc);

    public static PermissionConflictRuleChangeRequest ForDelete(
        Guid id, Guid? tenantId, Guid targetRuleId, long expectedRuleVersion, string permissionA, string permissionB,
        string? requestReason, string? ticketId, Guid? requestedByUserId, DateTime createdAtUtc)
        => new(id, tenantId, PermissionConflictRuleChangeType.Delete, targetRuleId, expectedRuleVersion,
            permissionA, permissionB, ruleReason: null, requestReason, ticketId, requestedByUserId, createdAtUtc);

    /// <summary>Marks the request applied. The caller applies the rule mutation in the same transaction.</summary>
    public void Approve(Guid deciderUserId, string? decisionReason, DateTime decidedAtUtc)
    {
        EnsureDecidable(deciderUserId);
        Status = PermissionConflictRuleChangeStatus.Applied;
        SetDecision(deciderUserId, decisionReason, decidedAtUtc);
    }

    public void Reject(Guid deciderUserId, string? decisionReason, DateTime decidedAtUtc)
    {
        EnsureDecidable(deciderUserId);
        Status = PermissionConflictRuleChangeStatus.Rejected;
        SetDecision(deciderUserId, decisionReason, decidedAtUtc);
    }

    /// <summary>The original requester withdraws their own pending request.</summary>
    public void Cancel(Guid requesterUserId, string? reason, DateTime cancelledAtUtc)
    {
        EnsurePending();
        if (RequestedByUserId is null || RequestedByUserId.Value != requesterUserId)
        {
            throw new InvalidOperationException("Only the original requester can cancel a conflict-rule change request.");
        }

        Status = PermissionConflictRuleChangeStatus.Cancelled;
        SetDecision(requesterUserId, reason, cancelledAtUtc);
    }

    private void EnsureDecidable(Guid deciderUserId)
    {
        EnsurePending();
        if (RequestedByUserId is not null && RequestedByUserId.Value == deciderUserId)
        {
            throw new InvalidOperationException("A conflict-rule change request cannot be decided by its own requester.");
        }
    }

    private void SetDecision(Guid deciderUserId, string? decisionReason, DateTime decidedAtUtc)
    {
        DecidedByUserId = deciderUserId;
        DecisionReason = Normalize(decisionReason, PermissionGrantConsts.MaxReasonLength);
        DecidedAtUtc = decidedAtUtc;
        Version++;
    }

    private void EnsurePending()
    {
        if (Status != PermissionConflictRuleChangeStatus.Pending)
        {
            throw new InvalidOperationException($"Conflict-rule change request is already {Status} and cannot be modified.");
        }
    }

    private static string? Normalize(string? value, int maxLength)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Check.NotNullOrWhiteSpace(value, nameof(value), maxLength: maxLength);
    }
}
