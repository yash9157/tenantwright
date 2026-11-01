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

namespace Permissions.Controllers.PermissionEvaluation;

[ApiController]
[Route("api/permissions/permission-evaluation")]
public class PermissionEvaluationController : ControllerBase
{
    private readonly IPermissionEvaluationAppService _service;

    public PermissionEvaluationController(IPermissionEvaluationAppService service)
    {
        _service = service;
    }

    [HttpPost("batch")]
    [Authorize(Policy = PermissionsHostPolicies.EvaluatePermissionsScope)]
    [Authorize(Policy = PermissionsPolicies.EvaluatePermissions)]
    [Audited]
    public Task<EvaluatePermissionsResultDto> EvaluateAsync([FromBody] EvaluatePermissionsRequestDto input, CancellationToken cancellationToken)
    {
        return _service.EvaluateAsync(input, cancellationToken);
    }

    [HttpGet("me")]
    [Authorize]
    [Audited]
    public Task<GetMyPermissionsResultDto> GetMyPermissionsAsync(CancellationToken cancellationToken)
    {
        return _service.GetMyPermissionsAsync(cancellationToken);
    }

    [HttpPost("me/explain")]
    [Authorize]
    [Audited]
    public Task<ExplainPermissionResultDto> ExplainMyPermissionAsync(
        [FromBody] ExplainMyPermissionRequestDto input,
        CancellationToken cancellationToken)
    {
        return _service.ExplainMyPermissionAsync(input, cancellationToken);
    }

    [HttpPost("explain")]
    [Authorize(Policy = PermissionsHostPolicies.EvaluatePermissionsScope)]
    [Authorize(Policy = PermissionsPolicies.EvaluatePermissions)]
    [Audited]
    public Task<ExplainPermissionResultDto> ExplainAsync([FromBody] ExplainPermissionRequestDto input, CancellationToken cancellationToken)
    {
        return _service.ExplainAsync(input, cancellationToken);
    }
}
