using Identity.Application.Contracts.SecurityAnalytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.SecurityAnalytics;

[ApiController]
[Route("api/identity/security-analytics")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.SecurityAnalytics.Default)]
public sealed class SecurityAnalyticsController : ControllerBase
{
    private readonly IIdentitySecurityAnalyticsAppService _securityAnalyticsAppService;

    public SecurityAnalyticsController(IIdentitySecurityAnalyticsAppService securityAnalyticsAppService)
    {
        _securityAnalyticsAppService = securityAnalyticsAppService;
    }

    [HttpGet("admin")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.SecurityAnalytics.Admin)]
    public Task<AdminSecurityAnalyticsDto> GetAdminAsync([FromQuery] int? windowDays = null)
    {
        return _securityAnalyticsAppService.GetAdminAsync(windowDays);
    }

    [HttpGet("admin/timing-heatmap")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.SecurityAnalytics.Admin)]
    public Task<SecurityTimingHeatmapDto> GetAdminTimingHeatmapAsync(
        [FromQuery] int? windowDays = null,
        [FromQuery] string? outcome = null)
    {
        return _securityAnalyticsAppService.GetAdminTimingHeatmapAsync(windowDays, outcome);
    }

    [HttpGet("tenant")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.SecurityAnalytics.Tenant)]
    public Task<TenantSecurityAnalyticsDto> GetTenantAsync([FromQuery] int? windowDays = null)
    {
        return _securityAnalyticsAppService.GetTenantAsync(windowDays);
    }
}
