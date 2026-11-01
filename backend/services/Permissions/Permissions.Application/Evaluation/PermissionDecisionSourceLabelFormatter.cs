using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Evaluation;

public static class PermissionDecisionSourceLabelFormatter
{
    public static string Format(PermissionDecisionSource source)
    {
        return source switch
        {
            PermissionDecisionSource.UserExplicitDeny => "User explicit deny",
            PermissionDecisionSource.UserExplicitAllow => "User explicit allow",
            PermissionDecisionSource.RoleExplicitDeny => "Role explicit deny",
            PermissionDecisionSource.RoleExplicitAllow => "Role explicit allow",
            PermissionDecisionSource.WildcardAllow => "Wildcard allow",
            PermissionDecisionSource.WildcardDeny => "Wildcard deny",
            PermissionDecisionSource.InheritedAllow => "Inherited allow",
            PermissionDecisionSource.FeatureDenied => "Feature denied",
            PermissionDecisionSource.NotDefined => "Not defined",
            PermissionDecisionSource.Disabled => "Disabled",
            PermissionDecisionSource.ConflictDenied => "Denied by separation-of-duty rule",
            _ => "Not granted"
        };
    }
}
