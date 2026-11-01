using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Shared.Permissions.Authorization;

public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionAuthorizationRequirement>
{
    private readonly IPermissionChecker _permissionChecker;

    public PermissionAuthorizationHandler(IPermissionChecker permissionChecker)
    {
        _permissionChecker = permissionChecker;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionAuthorizationRequirement requirement)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var granted = await _permissionChecker.IsGrantedAsync(context.User, requirement.PermissionName, CancellationToken.None);
        if (granted)
        {
            context.Succeed(requirement);
        }
    }
}
