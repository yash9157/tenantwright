using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.PlatformAssignments;
using Permissions.Application.PlatformAssignments;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Identity;

namespace Permissions.Controllers.PlatformAssignments;

[ApiController]
[Route("api/permissions/platform/access/assignments")]
[Authorize(Roles = "admin,HostAdmin")]
public sealed class PlatformPermissionAssignmentsController : ControllerBase
{
    private readonly IPlatformPermissionAssignmentAppService _service;

    public PlatformPermissionAssignmentsController(IPlatformPermissionAssignmentAppService service)
    {
        _service = service;
    }

    [HttpGet("roles/{roleId:guid}")]
    [Authorize(Policy = IdentityPermissions.PlatformRoles.ManagePermissions)]
    public Task<PlatformPermissionAssignmentDto> GetRoleAsync(Guid roleId, CancellationToken cancellationToken)
        => _service.GetRoleAsync(roleId, cancellationToken);

    [HttpPost("roles/{roleId:guid}/changes")]
    [Authorize(Policy = IdentityPermissions.PlatformRoles.ManagePermissions)]
    [Audited(Category = "permissions.grant")]
    public Task<ActionResult<PermissionAssignmentUpdateResultDto>> UpdateRoleAsync(
        Guid roleId,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        [FromBody] UpdatePlatformPermissionAssignmentRequest input,
        CancellationToken cancellationToken)
        => ExecuteUpdateAsync(() => _service.UpdateRoleAsync(roleId, idempotencyKey, input, cancellationToken));

    [HttpGet("users/{userId:guid}")]
    [Authorize(Policy = IdentityPermissions.Users.ManagePermissions)]
    public Task<PlatformPermissionAssignmentDto> GetUserAsync(Guid userId, CancellationToken cancellationToken)
        => _service.GetUserAsync(userId, cancellationToken);

    [HttpPost("users/{userId:guid}/changes")]
    [Authorize(Policy = IdentityPermissions.Users.ManagePermissions)]
    [Audited(Category = "permissions.grant")]
    public Task<ActionResult<PermissionAssignmentUpdateResultDto>> UpdateUserAsync(
        Guid userId,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        [FromBody] UpdatePlatformPermissionAssignmentRequest input,
        CancellationToken cancellationToken)
        => ExecuteUpdateAsync(() => _service.UpdateUserAsync(userId, idempotencyKey, input, cancellationToken));

    private static async Task<ActionResult<PermissionAssignmentUpdateResultDto>> ExecuteUpdateAsync(
        Func<Task<PermissionAssignmentUpdateResultDto>> action)
    {
        try
        {
            return await action();
        }
        catch (PlatformPermissionAssignmentConflictException exception)
        {
            return new ConflictObjectResult(exception.Conflict);
        }
    }
}
