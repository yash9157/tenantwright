namespace Permissions.Application.Contracts.PermissionConflictRules;

/// <summary>
/// Typed optimistic-concurrency / lifecycle conflict for a conflict-rule change. Surfaced as HTTP 409
/// so the client can reconcile against <see cref="CurrentVersion"/> instead of silently overwriting.
/// </summary>
public sealed class PermissionConflictRuleChangeConflictDto
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public long? CurrentVersion { get; init; }
}

/// <summary>Stable conflict codes for conflict-rule change reconciliation.</summary>
public static class PermissionConflictRuleChangeConflictCodes
{
    public const string RuleVersionMismatch = "conflict-rule-version-mismatch";
    public const string RequestVersionMismatch = "conflict-rule-request-version-mismatch";
    public const string RequestAlreadyDecided = "conflict-rule-request-already-decided";
    public const string DuplicateRule = "conflict-rule-duplicate";
    public const string PendingRequestExists = "conflict-rule-pending-request-exists";
    public const string RuleNotFound = "conflict-rule-not-found";
}
