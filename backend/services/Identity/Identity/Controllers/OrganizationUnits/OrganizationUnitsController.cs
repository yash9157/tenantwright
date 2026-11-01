using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Application.Contracts.OrganizationUnits;
using Shared.Permissions.Definitions.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.AuditLogs.Auditing;

namespace Identity.Controllers.OrganizationUnits;

[ApiController]
[Route("api/identity/organization-units")]
[Authorize(Policy = IdentityPermissions.OrganizationUnits.Default)]
public class OrganizationUnitsController : ControllerBase
{
    private readonly IOrganizationUnitAppService _organizationUnitAppService;

    public OrganizationUnitsController(IOrganizationUnitAppService organizationUnitAppService)
    {
        _organizationUnitAppService = organizationUnitAppService;
    }

    [HttpGet("{id}")]
    public Task<OrganizationUnitDto> GetAsync(Guid id)
    {
        return _organizationUnitAppService.GetAsync(id);
    }

    [HttpGet]
    public Task<List<OrganizationUnitDto>> GetListAsync()
    {
        return _organizationUnitAppService.GetListAsync();
    }

    [HttpGet("{id}/users")]
    [Authorize(Policy = IdentityPermissions.OrganizationUnits.ManageUsers)]
    public Task<List<OrganizationUnitUserDto>> GetUsersAsync(Guid id)
    {
        return _organizationUnitAppService.GetUsersAsync(id);
    }

    [HttpGet("{id}/roles")]
    [Authorize(Policy = IdentityPermissions.OrganizationUnits.ManageRoles)]
    public Task<List<OrganizationUnitRoleDto>> GetRolesAsync(Guid id)
    {
        return _organizationUnitAppService.GetRolesAsync(id);
    }

    [HttpPost]
    [Authorize(Policy = IdentityPermissions.OrganizationUnits.Create)]
    [Audited]
    public Task<OrganizationUnitDto> CreateAsync([FromBody] OrganizationUnitCreateDto input)
    {
        return _organizationUnitAppService.CreateAsync(input);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = IdentityPermissions.OrganizationUnits.Update)]
    [Audited]
    public Task<OrganizationUnitDto> UpdateAsync(Guid id, [FromBody] OrganizationUnitUpdateDto input)
    {
        return _organizationUnitAppService.UpdateAsync(id, input);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = IdentityPermissions.OrganizationUnits.Delete)]
    [Audited]
    public Task DeleteAsync(Guid id)
    {
        return _organizationUnitAppService.DeleteAsync(id);
    }

    [HttpPost("{id}/roles/{roleId}")]
    [Authorize(Policy = IdentityPermissions.OrganizationUnits.ManageRoles)]
    [Audited]
    public Task AddRoleAsync(Guid id, string roleId)
    {
        return _organizationUnitAppService.AddRoleAsync(id, roleId);
    }

    [HttpDelete("{id}/roles/{roleId}")]
    [Authorize(Policy = IdentityPermissions.OrganizationUnits.ManageRoles)]
    [Audited]
    public Task RemoveRoleAsync(Guid id, string roleId)
    {
        return _organizationUnitAppService.RemoveRoleAsync(id, roleId);
    }

    [HttpPost("{id}/users/{userId}")]
    [Authorize(Policy = IdentityPermissions.OrganizationUnits.ManageUsers)]
    [Audited]
    public Task AddUserAsync(Guid id, string userId)
    {
        return _organizationUnitAppService.AddUserAsync(id, userId);
    }

    [HttpDelete("{id}/users/{userId}")]
    [Authorize(Policy = IdentityPermissions.OrganizationUnits.ManageUsers)]
    [Audited]
    public Task RemoveUserAsync(Guid id, string userId)
    {
        return _organizationUnitAppService.RemoveUserAsync(id, userId);
    }
}
