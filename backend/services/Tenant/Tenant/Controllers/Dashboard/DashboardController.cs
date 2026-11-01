using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Permissions.Definitions.Tenant;
using Tenant.Application.Contracts.Dashboard;

namespace Tenant.Controllers.Dashboard;

[ApiController]
[Route("api/tenant/dashboard")]
[Authorize(Policy = TenantPermissions.Dashboard.Default)]
public sealed class DashboardController : ControllerBase
{
    private readonly ITenantDashboardAppService _dashboardAppService;

    public DashboardController(ITenantDashboardAppService dashboardAppService)
    {
        _dashboardAppService = dashboardAppService;
    }

    [HttpGet("admin")]
    [Authorize(Policy = TenantPermissions.Dashboard.Admin)]
    public Task<AdminTenantDashboardDto> GetAdminAsync([FromQuery] int? windowDays = null)
    {
        return _dashboardAppService.GetAdminAsync(windowDays);
    }

    [HttpGet("admin/onboarding-funnel")]
    [Authorize(Policy = TenantPermissions.Dashboard.Admin)]
    public Task<TenantOnboardingFunnelDto> GetOnboardingFunnelAsync(
        [FromQuery] int? windowDays = null,
        [FromQuery] int? stallThresholdHours = null)
    {
        return _dashboardAppService.GetOnboardingFunnelAsync(windowDays, stallThresholdHours);
    }
}
