namespace Shared.Permissions.Authorization;

public sealed class RoleIdentityPermissionOptions
{
    public const string SectionName = "Permissions:RoleIdentity";

    public int MaxActiveRoleMemberships { get; set; } = 32;
}
