namespace Permissions.Domain.Shared.Enums;

/// <summary>
/// Lifecycle of an approval-gated grant change (flow 6). A request starts Pending; approving it
/// applies the grant and moves it to Applied; it can otherwise be Rejected (by an approver) or
/// Cancelled (by the requester).
/// </summary>
public enum PermissionGrantChangeRequestStatus
{
    Pending = 1,
    Applied = 2,
    Rejected = 3,
    Cancelled = 4
}
