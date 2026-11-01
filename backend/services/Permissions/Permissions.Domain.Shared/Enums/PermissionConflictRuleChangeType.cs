namespace Permissions.Domain.Shared.Enums;

/// <summary>
/// The kind of change a <c>PermissionConflictRuleChangeRequest</c> proposes against the
/// separation-of-duty rule set. <see cref="Update"/> and <see cref="Delete"/> target an existing rule
/// by id and expected version; <see cref="Create"/> introduces a new pair.
/// </summary>
public enum PermissionConflictRuleChangeType
{
    Create = 1,
    Update = 2,
    Delete = 3
}
