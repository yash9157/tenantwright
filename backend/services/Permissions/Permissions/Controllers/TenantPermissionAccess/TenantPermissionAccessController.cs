using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;

namespace Permissions.Controllers.TenantPermissionAccess;

[ApiController]
[Route("api/permissions/current-tenant/access")]
[Authorize(Policy = PermissionsPolicies.ReadTenantAccess)]
public class TenantPermissionAccessController : ControllerBase
{
    private readonly ITenantPermissionAccessAppService _service;

    public TenantPermissionAccessController(ITenantPermissionAccessAppService service)
    {
        _service = service;
    }

    [HttpGet]
    public Task<TenantPermissionAccessDto> GetAsync([FromQuery] GetCurrentTenantPermissionAccessInput input, CancellationToken cancellationToken)
    {
        return _service.GetCurrentTenantAccessAsync(input, cancellationToken);
    }

    /// <summary>Effective access for the signed-in principal, host operators included (no active tenant required).</summary>
    [HttpGet("~/api/permissions/current-principal/access")]
    public Task<TenantPermissionAccessDto> GetPrincipalAsync([FromQuery] GetCurrentTenantPermissionAccessInput input, CancellationToken cancellationToken)
    {
        return _service.GetCurrentPrincipalAccessAsync(input, cancellationToken);
    }
}
