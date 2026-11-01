using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.UserDelegation;

[ApiController]
[Route("api/identity/user-delegations")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
public class UserDelegationController : ControllerBase
{
    private readonly IIdentityUserDelegationAppService _delegationAppService;

    public UserDelegationController(IIdentityUserDelegationAppService delegationAppService)
    {
        _delegationAppService = delegationAppService;
    }

    [HttpGet]
    public Task<List<IdentityUserDelegationDto>> GetListAsync()
    {
        return _delegationAppService.GetListAsync();
    }

    [HttpGet("admin")]
    [Authorize(Policy = IdentityPermissions.UserDelegation.Manage)]
    public Task<List<IdentityUserDelegationDto>> GetListForSourceAsync([FromQuery] Guid sourceUserId)
    {
        return _delegationAppService.GetListForSourceAsync(sourceUserId);
    }

    [HttpGet("active")]
    public Task<List<IdentityUserDelegationDto>> GetActiveDelegationsAsync()
    {
        return _delegationAppService.GetActiveDelegationsAsync();
    }

    [HttpGet("admin/active")]
    [Authorize(Policy = IdentityPermissions.UserDelegation.Manage)]
    public Task<List<IdentityUserDelegationDto>> GetActiveDelegationsForTargetAsync([FromQuery] Guid targetUserId)
    {
        return _delegationAppService.GetActiveDelegationsForTargetAsync(targetUserId);
    }

    [HttpPost]
    [Audited]
    public Task DelegateNewUserAsync([FromBody] CreateUserDelegationDto input)
    {
        return _delegationAppService.DelegateNewUserAsync(input);
    }

    [HttpPost("admin")]
    [Authorize(Policy = IdentityPermissions.UserDelegation.Manage)]
    [Audited]
    public Task DelegateNewUserForSourceAsync([FromBody] CreateUserDelegationForSourceDto input)
    {
        return _delegationAppService.DelegateNewUserForSourceAsync(input);
    }

    [HttpDelete("{id}")]
    [Audited]
    public Task DeleteDelegationAsync(Guid id)
    {
        return _delegationAppService.DeleteDelegationAsync(id);
    }

    [HttpDelete("admin/{id}")]
    [Authorize(Policy = IdentityPermissions.UserDelegation.Manage)]
    [Audited]
    public Task DeleteDelegationForSourceAsync(Guid id, [FromQuery] Guid sourceUserId)
    {
        return _delegationAppService.DeleteDelegationForSourceAsync(id, sourceUserId);
    }
}
