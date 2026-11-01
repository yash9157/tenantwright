using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Application.Contracts.SecurityLogs;
using Shared.Permissions.Definitions.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.AuditLogs.Auditing;

namespace Identity.Controllers.SecurityLogs;

[ApiController]
[Route("api/identity/security-logs")]
[Authorize(Policy = IdentityPermissions.SecurityLogs.Default)]
public class SecurityLogsController : ControllerBase
{
    private readonly ISecurityLogAppService _securityLogAppService;

    public SecurityLogsController(ISecurityLogAppService securityLogAppService)
    {
        _securityLogAppService = securityLogAppService;
    }

    [HttpGet("{id}")]
    public Task<SecurityLogDto> GetAsync(Guid id)
    {
        return _securityLogAppService.GetAsync(id);
    }

    [HttpGet]
    public Task<List<SecurityLogDto>> GetListAsync([FromQuery] string? userName, [FromQuery] string? clientId)
    {
        return _securityLogAppService.GetListAsync(userName, clientId);
    }

    [HttpGet("{id}/detail")]
    [Authorize(Policy = IdentityPermissions.SecurityLogs.ReadDetails)]
    public Task<SecurityLogDetailDto> GetDetailAsync(Guid id)
    {
        return _securityLogAppService.GetDetailAsync(id);
    }

    [HttpGet("paged")]
    public Task<PagedResultDto<SecurityLogDto>> GetPagedListAsync([FromQuery] SecurityLogListInput input)
    {
        return _securityLogAppService.GetPagedListAsync(input);
    }
}
