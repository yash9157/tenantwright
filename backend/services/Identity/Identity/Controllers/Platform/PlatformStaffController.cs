using Identity.Application.Contracts.Platform;
using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Identity;
using Identity.Application.Contracts.Mutations;
using Identity.Application.Contracts.Invitations;

namespace Identity.Controllers.Platform;

[ApiController]
[Route("api/identity/platform/staff")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Default)]
public sealed class PlatformStaffController : ControllerBase
{
    private readonly IPlatformStaffAppService _platformStaffAppService;
    private readonly IInvitationAppService _invitationAppService;

    public PlatformStaffController(
        IPlatformStaffAppService platformStaffAppService,
        IInvitationAppService invitationAppService)
    {
        _platformStaffAppService = platformStaffAppService;
        _invitationAppService = invitationAppService;
    }

    [HttpGet("invitations")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaffInvitations.Default)]
    public Task<PagedResultDto<PlatformStaffInvitationDto>> GetInvitationListAsync([FromQuery] PlatformStaffInvitationListInput input)
        => _invitationAppService.GetPlatformStaffListAsync(input);

    [HttpPost("invitations")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaffInvitations.Create)]
    [Audited]
    public Task<PlatformStaffInvitationDto> CreateInvitationAsync([FromBody] CreatePlatformStaffInvitationInput input)
        => _invitationAppService.CreatePlatformStaffAsync(input);

    [HttpPost("invitations/{id:guid}/resend")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaffInvitations.Resend)]
    [Audited]
    public Task<PlatformStaffInvitationDto> ResendInvitationAsync(Guid id)
        => _invitationAppService.ResendPlatformStaffAsync(id);

    [HttpDelete("invitations/{id:guid}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaffInvitations.Cancel)]
    [Audited]
    public Task CancelInvitationAsync(Guid id)
        => _invitationAppService.CancelPlatformStaffAsync(id);

    [HttpGet("{id}")]
    public Task<PlatformStaffDetailDto> GetAsync(string id)
    {
        return _platformStaffAppService.GetAsync(id);
    }

    [HttpGet]
    public Task<PagedResultDto<PlatformStaffDto>> GetPagedListAsync([FromQuery] GetPlatformStaffInput input)
    {
        return _platformStaffAppService.GetPagedListAsync(input);
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Create)]
    [Audited]
    public Task<PlatformStaffDto> CreateAsync([FromBody] PlatformStaffCreateDto input)
    {
        return _platformStaffAppService.CreateAsync(input);
    }

    [HttpPut("{id}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Update)]
    [Audited]
    public Task<PlatformStaffDto> UpdateAsync(string id, [FromBody] PlatformStaffUpdateDto input)
    {
        return _platformStaffAppService.UpdateAsync(id, input);
    }

    [HttpPut("{id}/roles")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.ManageRoles)]
    [Audited]
    public Task<PlatformStaffDto> SetRolesAsync(string id, [FromBody] IdentityRoleAssignmentInput input)
        => _platformStaffAppService.SetRolesAsync(id, input);

    [HttpPost("{id}/activate")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Activate)]
    [Audited]
    public Task<PlatformStaffDto> ActivateAsync(string id, [FromBody] IdentityActiveStateChangeInput input)
        => _platformStaffAppService.ActivateAsync(id, input);

    [HttpPost("{id}/deactivate")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Deactivate)]
    [Audited]
    public Task<PlatformStaffDto> DeactivateAsync(string id, [FromBody] IdentityActiveStateChangeInput input)
        => _platformStaffAppService.DeactivateAsync(id, input);

    [HttpPost("{id}/suspend")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Suspend)]
    [Audited]
    public Task<PlatformStaffDto> SuspendAsync(string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _platformStaffAppService.SuspendAsync(id, input);

    [HttpPost("{id}/restore")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Restore)]
    [Audited]
    public Task<PlatformStaffDto> RestoreAsync(string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _platformStaffAppService.RestoreAsync(id, input);

    [HttpPost("{id}/terminate")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Terminate)]
    [Audited]
    public Task<PlatformStaffDto> TerminateAsync(string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _platformStaffAppService.TerminateAsync(id, input);

    [HttpPost("{id}/rehire")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Rehire)]
    [Audited]
    public Task<PlatformStaffDto> RehireAsync(string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _platformStaffAppService.RehireAsync(id, input);

    [HttpPost("{id}/archive")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Archive)]
    [Audited]
    public Task<PlatformStaffDto> ArchiveAsync(string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _platformStaffAppService.ArchiveAsync(id, input);

    [HttpGet("{id}/lifecycle-history")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.LifecycleHistory)]
    public Task<PagedResultDto<IdentityUserLifecycleHistoryDto>> GetLifecycleHistoryAsync(
        string id,
        [FromQuery] GetIdentityLifecycleHistoryInput input)
        => _platformStaffAppService.GetLifecycleHistoryAsync(id, input);

    [HttpPost("{id}/lock")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Lock)]
    [Audited]
    public Task LockAsync(string id)
    {
        return _platformStaffAppService.LockAsync(id);
    }

    [HttpPost("{id}/unlock")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Unlock)]
    [Audited]
    public Task UnlockAsync(string id)
    {
        return _platformStaffAppService.UnlockAsync(id);
    }

    [HttpDelete("{id}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.PlatformStaff.Delete)]
    [Audited]
    public Task DeleteAsync(string id)
    {
        return _platformStaffAppService.DeleteAsync(id);
    }
}
