namespace Permissions.Domain.Shared.Enums;

/// <summary>
/// The kind of change recorded in the append-only permission grant history (gap claim 8).
/// One entry is written per affected permission at the moment a change is applied.
/// </summary>
public enum PermissionGrantHistoryChangeType
{
    /// <summary>A grant was created (allow or deny). See <c>NewEffect</c> for which.</summary>
    Granted = 1,

    /// <summary>An existing grant was removed.</summary>
    Revoked = 2,

    /// <summary>An existing grant's effect changed (e.g. Allow -> Deny). See <c>OldEffect</c>/<c>NewEffect</c>.</summary>
    EffectChanged = 3,

    /// <summary>A time-bound grant reached its expiry and was swept away.</summary>
    Expired = 4,

    /// <summary>A grant of an approval-gated permission was requested and is pending approval.</summary>
    ApprovalRequested = 5,

    /// <summary>A pending approval-gated grant request was rejected by an approver.</summary>
    ApprovalRejected = 6,

    /// <summary>A pending approval-gated grant request was cancelled by the requester (or superseded).</summary>
    ApprovalCancelled = 7
}
