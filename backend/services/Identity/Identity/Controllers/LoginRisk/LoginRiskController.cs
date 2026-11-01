using Identity.Application.Contracts.LoginRisk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.LoginRisk;

[ApiController]
[Route("api/identity/login-risk")]
[Authorize(
    AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
    Policy = IdentityPermissions.LoginRisk.Default)]
public sealed class LoginRiskController : ControllerBase
{
    private readonly ILoginRiskAdminAppService _loginRiskAdminAppService;

    public LoginRiskController(ILoginRiskAdminAppService loginRiskAdminAppService)
    {
        _loginRiskAdminAppService = loginRiskAdminAppService;
    }

    [HttpPost("overrides")]
    [Audited]
    [Authorize(
        AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        Policy = IdentityPermissions.LoginRisk.Override)]
    public Task<LoginRiskOverrideDto> CreateOverrideAsync([FromBody] LoginRiskOverrideCreateInput input)
    {
        return _loginRiskAdminAppService.CreateOverrideAsync(input);
    }

    [HttpGet("overrides")]
    public Task<List<LoginRiskOverrideDto>> GetOverridesAsync([FromQuery] Guid? userId, [FromQuery] bool includeInactive = false)
    {
        return _loginRiskAdminAppService.GetOverridesAsync(userId, includeInactive);
    }

    [HttpGet("overrides/paged")]
    public Task<PagedLoginRiskOverridesDto> GetPagedOverridesAsync([FromQuery] LoginRiskOverrideListInput input)
    {
        return _loginRiskAdminAppService.GetPagedOverridesAsync(input);
    }

    [HttpDelete("overrides/{id:guid}")]
    [Audited]
    [Authorize(
        AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        Policy = IdentityPermissions.LoginRisk.Override)]
    public Task RemoveOverrideAsync([FromRoute] Guid id)
    {
        return _loginRiskAdminAppService.RemoveOverrideAsync(id);
    }

    [HttpPost("decisions/{securityLogId:guid}/mark-false-positive")]
    [Audited]
    [Authorize(
        AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        Policy = IdentityPermissions.LoginRisk.FalsePositive)]
    public Task MarkFalsePositiveAsync([FromRoute] Guid securityLogId)
    {
        return _loginRiskAdminAppService.MarkFalsePositiveAsync(securityLogId);
    }
}
