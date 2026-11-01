using System;
using System.Threading.Tasks;
using Identity.Application.Contracts.Mutations;
using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.Platform;

[ApiController]
[Route("api/identity/platform/tenant-users")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Default)]
public sealed class PlatformTenantUsersController : ControllerBase
{
    private readonly IHostTenantUserAppService _hostTenantUserAppService;

    public PlatformTenantUsersController(IHostTenantUserAppService hostTenantUserAppService)
    {
        _hostTenantUserAppService = hostTenantUserAppService;
    }

    [HttpGet]
    public Task<PagedResultDto<UserDto>> GetPagedListAsync([FromQuery] GetHostTenantUsersInput input)
        => _hostTenantUserAppService.GetPagedListAsync(input);

    [HttpGet("{tenantId:guid}/{id}")]
    public Task<UserDetailDto> GetAsync(Guid tenantId, string id)
        => _hostTenantUserAppService.GetAsync(tenantId, id);

    [HttpPut("{tenantId:guid}/{id}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Update)]
    [Audited]
    public Task<UserDto> UpdateAsync(Guid tenantId, string id, [FromBody] UserUpdateDto input)
        => _hostTenantUserAppService.UpdateAsync(tenantId, id, input);

    [HttpPut("{tenantId:guid}/{id}/roles")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.ManageRoles)]
    [Audited]
    public Task<UserDto> SetRolesAsync(Guid tenantId, string id, [FromBody] IdentityRoleAssignmentInput input)
        => _hostTenantUserAppService.SetRolesAsync(tenantId, id, input);

    [HttpPost("{tenantId:guid}/{id}/suspend")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Suspend)]
    [Audited]
    public Task<UserDto> SuspendAsync(Guid tenantId, string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _hostTenantUserAppService.SuspendAsync(tenantId, id, input);

    [HttpPost("{tenantId:guid}/{id}/restore")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Restore)]
    [Audited]
    public Task<UserDto> RestoreAsync(Guid tenantId, string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _hostTenantUserAppService.RestoreAsync(tenantId, id, input);

    [HttpPost("{tenantId:guid}/{id}/terminate")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Terminate)]
    [Audited]
    public Task<UserDto> TerminateAsync(Guid tenantId, string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _hostTenantUserAppService.TerminateAsync(tenantId, id, input);

    [HttpPost("{tenantId:guid}/{id}/rehire")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Rehire)]
    [Audited]
    public Task<UserDto> RehireAsync(Guid tenantId, string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _hostTenantUserAppService.RehireAsync(tenantId, id, input);

    [HttpPost("{tenantId:guid}/{id}/archive")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Archive)]
    [Audited]
    public Task<UserDto> ArchiveAsync(Guid tenantId, string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _hostTenantUserAppService.ArchiveAsync(tenantId, id, input);

    [HttpGet("{tenantId:guid}/{id}/lifecycle-history")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.LifecycleHistory)]
    public Task<PagedResultDto<IdentityUserLifecycleHistoryDto>> GetLifecycleHistoryAsync(
        Guid tenantId,
        string id,
        [FromQuery] GetIdentityLifecycleHistoryInput input)
        => _hostTenantUserAppService.GetLifecycleHistoryAsync(tenantId, id, input);

    [HttpPost("{tenantId:guid}/{id}/lock")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Lock)]
    [Audited]
    public Task<UserDto> LockAsync(Guid tenantId, string id, [FromBody] IdentityLockStateChangeInput input)
        => _hostTenantUserAppService.LockAsync(tenantId, id, input);

    [HttpPost("{tenantId:guid}/{id}/unlock")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Unlock)]
    [Audited]
    public Task<UserDto> UnlockAsync(Guid tenantId, string id, [FromBody] IdentityLockStateChangeInput input)
        => _hostTenantUserAppService.UnlockAsync(tenantId, id, input);

    [HttpPost("{tenantId:guid}/primary-administrator")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.HandoverPrimaryAdministrator)]
    [Audited]
    public Task<TenantPrimaryAdminDto> HandoverPrimaryAdministratorAsync(
        Guid tenantId,
        [FromBody] PrimaryAdministratorHandoverInput input)
        => _hostTenantUserAppService.HandoverPrimaryAdministratorAsync(tenantId, input);
}
