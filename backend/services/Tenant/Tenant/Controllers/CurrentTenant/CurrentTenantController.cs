using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Permissions.Definitions.Tenant;
using Tenant.Application.Contracts.Tenants;
using Shared.AuditLogs.Auditing;

namespace Tenant.Controllers.CurrentTenant;

[ApiController]
[Route("api/tenant/current")]
[Authorize(Policy = TenantPermissions.Profile.Read)]
public sealed class CurrentTenantController : ControllerBase
{
    private readonly ITenantAppService _tenantAppService;

    public CurrentTenantController(ITenantAppService tenantAppService)
    {
        _tenantAppService = tenantAppService;
    }

    [HttpGet("profile")]
    public Task<CurrentTenantProfileDto> GetProfileAsync()
    {
        return _tenantAppService.GetCurrentProfileAsync();
    }

    [HttpGet("organization-profile")]
    public Task<TenantOrganizationProfileDto> GetOrganizationProfileAsync()
        => _tenantAppService.GetCurrentOrganizationProfileAsync();

    [HttpGet("closure-request")]
    [Authorize(Policy = TenantPermissions.Closure.Request)]
    public Task<TenantClosureStatusDto?> GetClosureRequestAsync()
        => _tenantAppService.GetCurrentClosureRequestAsync();

    [HttpPut("organization-profile")]
    [Authorize(Policy = TenantPermissions.Profile.Update)]
    [Audited(Category = "tenant.profile")]
    public Task<TenantOrganizationProfileDto> UpdateOrganizationProfileAsync([FromBody] TenantOrganizationProfileUpdateInput input)
        => _tenantAppService.UpdateCurrentOrganizationProfileAsync(input);
}
