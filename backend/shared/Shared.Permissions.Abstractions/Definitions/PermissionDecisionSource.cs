namespace Permissions.Domain.Shared.Enums;

public enum PermissionDecisionSource : byte
{
    NotGranted = 0,
    UserExplicitDeny = 1,
    UserExplicitAllow = 2,
    RoleExplicitDeny = 3,
    RoleExplicitAllow = 4,
    WildcardAllow = 5,
    WildcardDeny = 6,
    InheritedAllow = 7,
    FeatureDenied = 8,
    NotDefined = 9,
    Disabled = 10,
    ConflictDenied = 11
}
