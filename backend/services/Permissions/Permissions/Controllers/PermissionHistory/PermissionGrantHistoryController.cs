using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.PermissionHistory;

namespace Permissions.Controllers.PermissionHistory;

[ApiController]
[Route("api/permissions/grant-history")]
public class PermissionGrantHistoryController : ControllerBase
{
    private readonly IPermissionGrantHistoryAppService _service;

    public PermissionGrantHistoryController(IPermissionGrantHistoryAppService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = PermissionsPolicies.ReadGrantHistory)]
    public Task<PermissionGrantHistoryResultDto> GetListAsync(
        [FromQuery] GetPermissionGrantHistoryInput input,
        CancellationToken cancellationToken)
    {
        return _service.GetListAsync(input, cancellationToken);
    }
}
