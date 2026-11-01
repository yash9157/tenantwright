using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Application.Contracts.ClaimTypes;
using Identity.Application.Contracts.Users;
using Shared.Permissions.Definitions.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.AuditLogs.Auditing;

namespace Identity.Controllers.ClaimTypes;

[ApiController]
[Route("api/identity/claim-types")]
[Authorize(Policy = IdentityPermissions.ClaimTypes.Default)]
public class ClaimTypesController : ControllerBase
{
    private readonly IIdentityClaimTypeAppService _claimTypeAppService;

    public ClaimTypesController(IIdentityClaimTypeAppService claimTypeAppService)
    {
        _claimTypeAppService = claimTypeAppService;
    }

    [HttpGet("{id}")]
    public Task<ClaimTypeDto> GetAsync(Guid id)
    {
        return _claimTypeAppService.GetAsync(id);
    }

    [HttpGet]
    public Task<PagedResultDto<ClaimTypeDto>> GetListAsync([FromQuery] ClaimTypeListInput input)
    {
        return _claimTypeAppService.GetListAsync(input);
    }

    [HttpPost]
    [Authorize(Policy = IdentityPermissions.ClaimTypes.Create)]
    [Audited]
    public Task<ClaimTypeDto> CreateAsync([FromBody] ClaimTypeCreateDto input)
    {
        return _claimTypeAppService.CreateAsync(input);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = IdentityPermissions.ClaimTypes.Update)]
    [Audited]
    public Task<ClaimTypeDto> UpdateAsync(Guid id, [FromBody] ClaimTypeUpdateDto input)
    {
        return _claimTypeAppService.UpdateAsync(id, input);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = IdentityPermissions.ClaimTypes.Delete)]
    [Audited]
    public Task DeleteAsync(Guid id)
    {
        return _claimTypeAppService.DeleteAsync(id);
    }
}
