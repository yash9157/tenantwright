using Identity.Application.Contracts.Platform;
using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.Platform;

[ApiController]
[Route("api/identity/platform/permission-assignment-targets")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Roles = "admin,HostAdmin")]
public sealed class PermissionAssignmentTargetsController : ControllerBase
{
    private readonly IPermissionAssignmentTargetSearchAppService _service;

    public PermissionAssignmentTargetsController(IPermissionAssignmentTargetSearchAppService service)
    {
        _service = service;
    }

    [HttpGet("users")]
    [Authorize(Policy = IdentityPermissions.Users.ManagePermissions)]
    public Task<PagedResultDto<PermissionAssignmentUserTargetDto>> GetUsersAsync(
        [FromQuery] PermissionAssignmentTargetSearchInput input,
        CancellationToken cancellationToken)
        => _service.GetUsersAsync(input, cancellationToken);

    [HttpGet("roles")]
    [Authorize(Policy = IdentityPermissions.PlatformRoles.ManagePermissions)]
    public Task<PagedResultDto<PermissionAssignmentRoleTargetDto>> GetRolesAsync(
        [FromQuery] PermissionAssignmentTargetSearchInput input,
        CancellationToken cancellationToken)
        => _service.GetRolesAsync(input, cancellationToken);
}
