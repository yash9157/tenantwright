using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.AspNetCore.Host.Idempotency;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.Session;

[ApiController]
[Route("api/identity/sessions")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
public class SessionController : ControllerBase
{
    private readonly IIdentitySessionAppService _sessionAppService;

    public SessionController(IIdentitySessionAppService sessionAppService)
    {
        _sessionAppService = sessionAppService;
    }

    [HttpGet]
    public Task<List<IdentitySessionDto>> GetListAsync()
    {
        return _sessionAppService.GetListAsync();
    }

    [HttpGet("me")]
    public Task<List<IdentitySessionDto>> GetMySessionsAsync()
    {
        return _sessionAppService.GetListAsync();
    }

    [HttpDelete("{id}")]
    [Audited(Category = "auth.session")]
    public Task RevokeAsync(Guid id)
    {
        return _sessionAppService.RevokeAsync(id);
    }

    [HttpPost("revoke")]
    [Audited(Category = "auth.session")]
    public Task RevokeCurrentBySessionIdAsync([FromBody] RevokeOwnSessionInput input)
    {
        return _sessionAppService.RevokeCurrentBySessionIdAsync(input.SessionId);
    }

    [HttpPost("revoke-others")]
    [RequireIdempotency]
    [Audited(Category = "auth.session")]
    public Task<int> RevokeOthersAsync()
    {
        return _sessionAppService.RevokeOthersAsync();
    }

    [HttpGet("users/{userId:guid}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Sessions.Default)]
    public Task<List<IdentitySessionDto>> GetListByUserIdAsync(Guid userId)
    {
        return _sessionAppService.GetListByUserIdAsync(userId);
    }

    [HttpGet("users/{userId:guid}/paged")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Sessions.Default)]
    public Task<PagedResultDto<IdentitySessionDto>> GetPagedListByUserIdAsync(
        Guid userId,
        [FromQuery] GetUserSessionsPagedInput input)
    {
        return _sessionAppService.GetPagedListByUserIdAsync(userId, input);
    }

    [HttpPost("users/{userId:guid}/revoke-all")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Sessions.RevokeBulk)]
    [RequireIdempotency]
    [Audited(Category = "auth.session")]
    public Task<int> RevokeAllByUserIdAsync(
        Guid userId,
        [FromBody] ManagedBulkSessionRevokeInput? input = null)
    {
        return _sessionAppService.RevokeAllByUserIdAsync(userId, input?.Reason);
    }

    [HttpGet("management/paged")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Sessions.Default)]
    public Task<PagedResultDto<OperationalSessionDto>> GetManagementPagedListAsync([FromQuery] GetOperationalSessionsInput input)
    {
        return _sessionAppService.GetManagementPagedListAsync(input);
    }

    [HttpPost("management/revoke-selected")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Sessions.RevokeBulk)]
    [RequireIdempotency]
    [Audited(Category = "auth.session")]
    public Task<BulkRevokeSessionsResultDto> RevokeSelectedAsync([FromBody] BulkRevokeSessionsInput input)
    {
        return _sessionAppService.RevokeSelectedAsync(input);
    }

    [HttpPost("management/{sessionId:guid}/revoke")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Sessions.Revoke)]
    [RequireIdempotency]
    [Audited(Category = "auth.session")]
    public Task<BulkRevokeSessionsResultDto> RevokeManagedAsync(
        Guid sessionId,
        [FromBody] ManagedSessionRevokeInput input)
    {
        return _sessionAppService.RevokeManagedAsync(sessionId, input);
    }

    [HttpPost("management/tenants/{tenantId:guid}/revoke-all")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Sessions.RevokeTenant)]
    [RequireIdempotency]
    [Audited(Category = "auth.session")]
    public Task<int> RevokeAllByTenantIdAsync(
        Guid tenantId,
        [FromBody] ManagedBulkSessionRevokeInput? input = null)
    {
        return _sessionAppService.RevokeAllByTenantIdAsync(tenantId, input?.Reason);
    }
}
