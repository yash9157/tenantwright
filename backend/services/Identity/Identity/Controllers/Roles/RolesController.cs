using Identity.Application.Contracts.Roles;
using Shared.Permissions.Definitions.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared.AuditLogs.Auditing;
using OpenIddict.Validation.AspNetCore;

namespace Identity.Controllers.Roles;

[ApiController]
[Route("api/identity/roles")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
public class RolesController : ControllerBase
{
    private readonly IRoleAppService _roleAppService;

    public RolesController(IRoleAppService roleAppService)
    {
        _roleAppService = roleAppService;
    }

    [HttpGet("{id}")]
    [Authorize(Policy = IdentityPermissions.Roles.Default)]
    public Task<RoleDto> GetAsync(string id)
    {
        return _roleAppService.GetAsync(id);
    }

    [HttpGet]
    [Authorize(Policy = IdentityPermissions.Roles.Default)]
    public Task<List<RoleDto>> GetListAsync()
    {
        return _roleAppService.GetListAsync();
    }

    [HttpPost]
    [Authorize(Policy = IdentityPermissions.Roles.Create)]
    [Audited]
    public Task<RoleDto> CreateAsync([FromBody] RoleCreateDto input)
    {
        return _roleAppService.CreateAsync(input);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = IdentityPermissions.Roles.Update)]
    [Audited]
    public Task<RoleDto> UpdateAsync(string id, [FromBody] RoleUpdateDto input)
    {
        return _roleAppService.UpdateAsync(id, input);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = IdentityPermissions.Roles.Delete)]
    [Audited]
    public Task DeleteAsync(string id)
    {
        return _roleAppService.DeleteAsync(id);
    }
}
