namespace Permissions.Domain.Shared.Enums;

/// <summary>
/// Lifecycle of a four-eyes <c>PermissionConflictRuleChangeRequest</c>: a proposed create, update, or
/// delete of a separation-of-duty rule is <see cref="Pending"/> until a second operator with the
/// dedicated decide authority applies or rejects it. The requester may withdraw their own pending
/// request. Terminal states are immutable.
/// </summary>
public enum PermissionConflictRuleChangeStatus
{
    Pending = 1,
    Applied = 2,
    Rejected = 3,
    Cancelled = 4
}
