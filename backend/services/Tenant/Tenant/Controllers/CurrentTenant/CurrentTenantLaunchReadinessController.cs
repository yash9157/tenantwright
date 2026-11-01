using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Permissions.Definitions.Tenant;
using Tenant.Application.Contracts.Tenants;

namespace Tenant.Controllers.CurrentTenant;

[ApiController]
[Route("api/tenant/current/launch-readiness")]
[Authorize(Policy = TenantPermissions.Launch.Read)]
public sealed class CurrentTenantLaunchReadinessController(
    ITenantLaunchReadinessAppService service) : ControllerBase
{
    [HttpGet]
    public Task<TenantLaunchReadinessDto> GetAsync(CancellationToken cancellationToken) =>
        service.GetCurrentAsync(cancellationToken);
}
