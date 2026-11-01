using Identity.Application.Contracts.SecuritySettings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.SecuritySettings;

[ApiController]
[Route("api/identity/security-settings")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.SecuritySettings.Default)]
public sealed class SecuritySettingsController : ControllerBase
{
    private readonly IHostIdentitySecuritySettingsAppService _appService;

    public SecuritySettingsController(IHostIdentitySecuritySettingsAppService appService)
    {
        _appService = appService;
    }

    [HttpGet("host")]
    public Task<HostIdentitySecuritySettingsDto> GetHostAsync()
    {
        return _appService.GetHostAsync();
    }

    [HttpPut("host")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.SecuritySettings.Update)]
    public Task<HostIdentitySecuritySettingsDto> UpdateHostAsync([FromBody] UpdateHostIdentitySecuritySettingsInput input)
    {
        return _appService.UpdateHostAsync(input);
    }

    [HttpPost("host/preview")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.SecuritySettings.Update)]
    public Task<HostIdentitySecurityPolicyImpactDto> PreviewHostUpdateAsync([FromBody] UpdateHostIdentitySecuritySettingsInput input)
    {
        return _appService.PreviewHostUpdateAsync(input);
    }

    [HttpPut("host/governed")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.SecuritySettings.Update)]
    public Task<HostIdentitySecuritySettingsDto> UpdateHostGovernedAsync([FromBody] GovernedHostIdentitySecuritySettingsUpdateInput input)
    {
        return _appService.UpdateHostGovernedAsync(input);
    }
}
