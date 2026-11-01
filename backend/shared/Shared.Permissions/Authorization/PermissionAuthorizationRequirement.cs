using Microsoft.AspNetCore.Authorization;

namespace Shared.Permissions.Authorization;

public sealed class PermissionAuthorizationRequirement : IAuthorizationRequirement
{
    public string PermissionName { get; }

    public PermissionAuthorizationRequirement(string permissionName)
    {
        PermissionName = permissionName;
    }
}
