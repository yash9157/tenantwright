using Identity.Application.Contracts.Platform;
using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.Platform;

[ApiController]
[Route("api/identity/platform/roles")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformRoles.Default)]
public sealed class PlatformRolesController : ControllerBase
{
    private readonly IPlatformRoleAppService _platformRoleAppService;

    public PlatformRolesController(IPlatformRoleAppService platformRoleAppService)
    {
        _platformRoleAppService = platformRoleAppService;
    }

    [HttpGet("{id}")]
    public Task<PlatformRoleDto> GetAsync(string id)
    {
        return _platformRoleAppService.GetAsync(id);
    }

    [HttpGet]
    public Task<PagedResultDto<PlatformRoleDto>> GetListAsync([FromQuery] PlatformRoleListInput input)
    {
        return _platformRoleAppService.GetListAsync(input);
    }

    [HttpGet("{id}/members")]
    public Task<PagedResultDto<PlatformRoleMemberDto>> GetMembersAsync(
        string id,
        [FromQuery] PagedRequestDto input)
        => _platformRoleAppService.GetMembersAsync(id, input);

    [HttpGet("membership-cardinality-report")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformRoles.ManagePermissions)]
    public Task<RoleMembershipCardinalityReportDto> GetMembershipCardinalityReportAsync()
        => _platformRoleAppService.GetMembershipCardinalityReportAsync();

    [HttpPost]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformRoles.Create)]
    [Audited]
    public Task<PlatformRoleDto> CreateAsync([FromBody] PlatformRoleCreateDto input)
    {
        return _platformRoleAppService.CreateAsync(input);
    }

    [HttpPut("{id}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformRoles.Update)]
    [Audited]
    public Task<PlatformRoleDto> UpdateAsync(string id, [FromBody] PlatformRoleUpdateDto input)
    {
        return _platformRoleAppService.UpdateAsync(id, input);
    }

    [HttpPost("{id}/retirement")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformRoles.Delete)]
    [Audited]
    public Task<PlatformRoleDto> BeginRetirementAsync(string id)
    {
        return _platformRoleAppService.BeginRetirementAsync(id);
    }

    [HttpDelete("{id}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformRoles.Delete)]
    [Audited]
    public Task DeleteAsync(string id)
    {
        return _platformRoleAppService.DeleteAsync(id);
    }
}
