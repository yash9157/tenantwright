using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.PermissionHistory;
using Shared.MultiTenancy;

namespace Permissions.Controllers.PermissionHistory;

/// <summary>
/// Host-only platform-wide history read. The existing grant-history route remains tenant scoped.
/// </summary>
[ApiController]
[Route("api/permissions/platform/grant-history")]
public sealed class PlatformPermissionGrantHistoryController : ControllerBase
{
    private const string PlatformAccessRoles = "admin,HostAdmin";

    private readonly IPermissionGrantHistoryAppService _service;
    private readonly ICurrentTenant _currentTenant;

    public PlatformPermissionGrantHistoryController(
        IPermissionGrantHistoryAppService service,
        ICurrentTenant currentTenant)
    {
        _service = service;
        _currentTenant = currentTenant;
    }

    [HttpGet]
    [Authorize(Policy = PermissionsPolicies.ReadGrantHistory, Roles = PlatformAccessRoles)]
    public async Task<ActionResult<PermissionGrantHistoryResultDto>> GetListAsync(
        [FromQuery] GetPermissionGrantHistoryInput input,
        CancellationToken cancellationToken)
    {
        if (_currentTenant.IsAvailable)
        {
            return Forbid();
        }

        return Ok(await _service.GetPlatformListAsync(input, cancellationToken));
    }
}
