using Identity.Application.Contracts.InternalPermissionTargets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.AuditLogs.Auditing;

namespace Identity.Controllers.InternalPermissionTargets;

[ApiController]
[IntegrationService]
[Route("api/identity/internal/permission-targets")]
[Authorize(Policy = "IdentityInternal")]
public sealed class InternalPermissionTargetsController : ControllerBase
{
    private readonly IIdentityPermissionTargetValidationAppService _targetValidationAppService;

    public InternalPermissionTargetsController(IIdentityPermissionTargetValidationAppService targetValidationAppService)
    {
        _targetValidationAppService = targetValidationAppService;
    }

    [HttpGet("roles/{roleId:guid}")]
    public async Task<ActionResult<RolePermissionTargetDto>> GetRoleByIdAsync(
        Guid roleId,
        [FromQuery] Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var result = await _targetValidationAppService.GetRoleAsync(roleId, tenantId, cancellationToken);
        if (result == null)
        {
            return NotFound(new
            {
                error = "role_not_found",
                error_description = "Role target was not found in the requested scope."
            });
        }

        return Ok(result);
    }

    [HttpGet("users/{userId:guid}")]
    public async Task<ActionResult<UserPermissionTargetDto>> GetUserAsync(
        Guid userId,
        [FromQuery] Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var result = await _targetValidationAppService.GetUserAsync(userId, tenantId, cancellationToken);
        if (result == null)
        {
            return NotFound(new
            {
                error = "user_not_found",
                error_description = "User target was not found in the requested scope."
            });
        }

        return Ok(result);
    }

    [HttpPost("resolve")]
    public Task<List<ResolvedPermissionTargetDto>> ResolveAsync(
        [FromBody] ResolvePermissionTargetsInput input,
        CancellationToken cancellationToken)
    {
        return _targetValidationAppService.ResolveAsync(input, cancellationToken);
    }
}
