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
using Shared.AuditLogs.Auditing;

namespace Permissions.Controllers.FeatureCheck;

[ApiController]
[IntegrationService]
[Route("api/permissions/feature-check")]
[Authorize(Policy = PermissionsPolicies.CheckFeaturesInternal)]
public class FeatureCheckController : ControllerBase
{
    private readonly IFeatureManagementAppService _service;

    public FeatureCheckController(IFeatureManagementAppService service)
    {
        _service = service;
    }

    [HttpPost("batch")]
    public Task<BatchFeatureCheckResponseDto> BatchAsync([FromBody] BatchFeatureCheckRequestDto input, CancellationToken cancellationToken)
    {
        return _service.BatchCheckAsync(input, cancellationToken);
    }
}
