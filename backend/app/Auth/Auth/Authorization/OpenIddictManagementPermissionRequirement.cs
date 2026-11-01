using Microsoft.AspNetCore.Authorization;

namespace Auth.Authorization;

public sealed record OpenIddictManagementPermissionRequirement(string PermissionName)
    : IAuthorizationRequirement;
