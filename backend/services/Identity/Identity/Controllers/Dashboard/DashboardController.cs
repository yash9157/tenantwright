using Identity.Application.Contracts.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.Dashboard;

[ApiController]
[Route("api/identity/dashboard")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Dashboard.Default)]
public sealed class DashboardController : ControllerBase
{
    private readonly IIdentityDashboardAppService _dashboardAppService;

    public DashboardController(IIdentityDashboardAppService dashboardAppService)
    {
        _dashboardAppService = dashboardAppService;
    }

    [HttpGet("admin")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Dashboard.Admin)]
    public Task<AdminIdentityDashboardDto> GetAdminAsync([FromQuery] int? windowDays = null)
    {
        return _dashboardAppService.GetAdminAsync(windowDays);
    }

    [HttpGet("admin/mfa-adoption")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Dashboard.Admin)]
    public Task<MfaAdoptionDto> GetMfaAdoptionAsync([FromQuery] int? top = null)
    {
        return _dashboardAppService.GetMfaAdoptionAsync(top);
    }

    [HttpGet("tenant")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Dashboard.Tenant)]
    public Task<TenantIdentityDashboardDto> GetTenantAsync([FromQuery] int? windowDays = null)
    {
        return _dashboardAppService.GetTenantAsync(windowDays);
    }
}
