using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Permissions.Definitions.Tenant;
using Tenant.Application.Contracts.Dashboard;

namespace Tenant.Controllers.CurrentTenantDashboard;

[ApiController]
[Route("api/tenant/current")]
[Authorize(Policy = TenantPermissions.Dashboard.Tenant)]
public sealed class CurrentTenantDashboardController : ControllerBase
{
    private readonly ITenantDashboardAppService _dashboardAppService;

    public CurrentTenantDashboardController(ITenantDashboardAppService dashboardAppService)
    {
        _dashboardAppService = dashboardAppService;
    }

    [HttpGet("dashboard")]
    public Task<CurrentTenantDashboardDto> GetDashboardAsync()
    {
        return _dashboardAppService.GetCurrentAsync();
    }
}
