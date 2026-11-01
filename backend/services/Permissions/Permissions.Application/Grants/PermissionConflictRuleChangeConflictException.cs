using Permissions.Application.Contracts.PermissionConflictRules;

namespace Permissions.Application.Grants;

/// <summary>
/// Thrown when a conflict-rule change cannot proceed because of an optimistic-concurrency or
/// lifecycle conflict. The controller maps it to HTTP 409 with the carried
/// <see cref="PermissionConflictRuleChangeConflictDto"/>.
/// </summary>
public sealed class PermissionConflictRuleChangeConflictException : Exception
{
    public PermissionConflictRuleChangeConflictDto Conflict { get; }

    public PermissionConflictRuleChangeConflictException(PermissionConflictRuleChangeConflictDto conflict)
        : base($"Permission conflict-rule change conflict: {conflict.Code}.")
    {
        Conflict = conflict;
    }
}
