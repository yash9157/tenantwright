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
using Permissions.Application.FeatureManagement;
using Shared.AuditLogs.Auditing;

namespace Permissions.Controllers.FeatureManagement;

[ApiController]
[Route("api/permissions/feature-management/providers/{providerName}/{providerKey}")]
public class FeatureManagementController : ControllerBase
{
    private readonly IFeatureManagementAppService _service;

    public FeatureManagementController(IFeatureManagementAppService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = PermissionsPolicies.ReadFeatures)]
    public Task<GetFeatureListResultDto> GetAsync(string providerName, string providerKey, CancellationToken cancellationToken)
    {
        return _service.GetAsync(providerName, providerKey, cancellationToken);
    }

    [HttpPut]
    [Authorize(Policy = PermissionsPolicies.UpdateFeatures)]
    [Audited]
    public async Task<IActionResult> UpdateAsync(string providerName, string providerKey, [FromBody] UpdateFeaturesDto input, CancellationToken cancellationToken)
    {
        try
        {
            await _service.UpdateAsync(providerName, providerKey, input, cancellationToken);
            return NoContent();
        }
        catch (FeatureConcurrencyConflictException exception)
        {
            return Conflict(exception.Conflict);
        }
    }
}
