using Identity.Application.Contracts.Users;
using Shared.Permissions.Definitions.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;
using Identity.Application.Contracts.Mutations;

namespace Identity.Controllers.Users;

[ApiController]
[Route("api/identity/users")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Default)]
public class UsersController : ControllerBase
{
    private readonly IUserAppService _userAppService;

    public UsersController(IUserAppService userAppService)
    {
        _userAppService = userAppService;
    }

    [HttpGet("{id}")]
    public Task<UserDetailDto> GetAsync(string id)
    {
        return _userAppService.GetAsync(id);
    }

    [HttpGet]
    public Task<PagedResultDto<UserDto>> GetPagedListAsync([FromQuery] GetUsersInput input)
    {
        return _userAppService.GetPagedListAsync(input);
    }

    [HttpPost("bulk/activate")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.BulkUpdate)]
    [Audited]
    public Task<BulkUserActionResultDto> ActivateBulkAsync([FromBody] BulkUserActionInput input)
    {
        return _userAppService.ActivateBulkAsync(input);
    }

    [HttpPost("bulk/deactivate")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.BulkUpdate)]
    [Audited]
    public Task<BulkUserActionResultDto> DeactivateBulkAsync([FromBody] BulkUserActionInput input)
    {
        return _userAppService.DeactivateBulkAsync(input);
    }

    [HttpPost("bulk/revoke-sessions")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.BulkUpdate)]
    [Audited]
    public Task<BulkUserActionResultDto> RevokeSessionsBulkAsync([FromBody] BulkUserActionInput input)
    {
        return _userAppService.RevokeSessionsBulkAsync(input);
    }

    [HttpPost("exports")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Export)]
    [Audited]
    public Task<UserExportJobDto> CreateExportAsync([FromBody] RequestUserExportInput input)
    {
        return _userAppService.CreateExportAsync(input);
    }

    [HttpGet("exports/{id:long}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Export)]
    public Task<UserExportJobDto> GetExportAsync(long id)
    {
        return _userAppService.GetExportAsync(id);
    }

    [HttpGet("exports/{id:long}/download")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Export)]
    public async Task<IActionResult> GetExportDownloadAsync(long id, [FromQuery] string token)
    {
        var result = await _userAppService.GetExportDownloadAsync(id, token);
        return File(result.Content, result.ContentType, result.FileName);
    }

    [HttpPost("imports/validate")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Import)]
    [Audited]
    public Task<UserImportValidationDto> ValidateImportAsync([FromBody] ValidateUserImportInput input)
    {
        return _userAppService.ValidateImportAsync(input);
    }

    [HttpPost("imports")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Import)]
    [Audited]
    public Task<UserImportJobDto> StartImportAsync([FromBody] StartUserImportInput input)
    {
        return _userAppService.StartImportAsync(input);
    }

    [HttpGet("imports/{id:long}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Import)]
    public Task<UserImportJobDto> GetImportAsync(long id)
    {
        return _userAppService.GetImportAsync(id);
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Create)]
    [Audited]
    public Task<UserDto> CreateAsync([FromBody] UserCreateDto input)
    {
        return _userAppService.CreateAsync(input);
    }

    [HttpPut("{id}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Update)]
    [Audited]
    public Task<UserDto> UpdateAsync(string id, [FromBody] UserUpdateDto input)
    {
        return _userAppService.UpdateAsync(id, input);
    }

    [HttpPut("{id}/roles")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.ManageRoles)]
    [Audited]
    public Task<UserDto> SetRolesAsync(string id, [FromBody] IdentityRoleAssignmentInput input)
        => _userAppService.SetRolesAsync(id, input);

    [HttpPost("{id}/activate")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Activate)]
    [Audited]
    public Task<UserDto> ActivateAsync(string id, [FromBody] IdentityActiveStateChangeInput input)
        => _userAppService.ActivateAsync(id, input);

    [HttpPost("{id}/deactivate")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Deactivate)]
    [Audited]
    public Task<UserDto> DeactivateAsync(string id, [FromBody] IdentityActiveStateChangeInput input)
        => _userAppService.DeactivateAsync(id, input);

    [HttpPost("{id}/suspend")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Suspend)]
    [Audited]
    public Task<UserDto> SuspendAsync(string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _userAppService.SuspendAsync(id, input);

    [HttpPost("{id}/restore")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Restore)]
    [Audited]
    public Task<UserDto> RestoreAsync(string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _userAppService.RestoreAsync(id, input);

    [HttpPost("{id}/terminate")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Terminate)]
    [Audited]
    public Task<UserDto> TerminateAsync(string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _userAppService.TerminateAsync(id, input);

    [HttpPost("{id}/rehire")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Rehire)]
    [Audited]
    public Task<UserDto> RehireAsync(string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _userAppService.RehireAsync(id, input);

    [HttpPost("{id}/archive")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Archive)]
    [Audited]
    public Task<UserDto> ArchiveAsync(string id, [FromBody] IdentityLifecycleTransitionInput input)
        => _userAppService.ArchiveAsync(id, input);

    [HttpGet("{id}/lifecycle-history")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.LifecycleHistory)]
    public Task<PagedResultDto<IdentityUserLifecycleHistoryDto>> GetLifecycleHistoryAsync(
        string id,
        [FromQuery] GetIdentityLifecycleHistoryInput input)
        => _userAppService.GetLifecycleHistoryAsync(id, input);

    [HttpPost("{id}/lock")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Lock)]
    [Audited]
    public Task LockAsync(string id)
    {
        return _userAppService.LockAsync(id);
    }

    [HttpPost("{id}/unlock")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Unlock)]
    [Audited]
    public Task UnlockAsync(string id)
    {
        return _userAppService.UnlockAsync(id);
    }

    [HttpDelete("{id}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Delete)]
    [Audited]
    public Task DeleteAsync(string id)
    {
        return _userAppService.DeleteAsync(id);
    }
}
