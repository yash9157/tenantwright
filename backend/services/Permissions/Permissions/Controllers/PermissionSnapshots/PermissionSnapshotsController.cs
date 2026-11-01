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
using Shared.Security;
using Shared.AuditLogs.Auditing;

namespace Permissions.Controllers.PermissionSnapshots;

[ApiController]
[Route("api/permissions/permission-snapshots")]
public class PermissionSnapshotsController : ControllerBase
{
    private readonly IPermissionSnapshotAppService _service;

    public PermissionSnapshotsController(IPermissionSnapshotAppService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = PermissionsPolicies.ReadSnapshots)]
    public Task<PermissionSnapshotDto> GetAsync(CancellationToken cancellationToken)
    {
        var userId = ResolveCurrentUserId();
        var tenantId = ResolveCurrentTenantId();
        var roleIds = ResolveCurrentRoleIds();
        return _service.GetSnapshotAsync(tenantId, userId, roleIds, cancellationToken);
    }

    private Guid ResolveCurrentUserId()
    {
        var subject = User.FindFirst(SharedClaimTypes.Subject)?.Value
            ?? User.FindFirst(SharedClaimTypes.UserId)?.Value;
        if (!Guid.TryParse(subject, out var userId) || userId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Current user id is required for permission snapshots.");
        }

        return userId;
    }

    private Guid? ResolveCurrentTenantId()
    {
        var tenant = User.FindFirst(SharedClaimTypes.TenantId)?.Value
            ?? User.FindFirst(SharedClaimTypes.LegacyTenantId)?.Value;
        return Guid.TryParse(tenant, out var tenantId) && tenantId != Guid.Empty
            ? tenantId
            : null;
    }

    private List<Guid> ResolveCurrentRoleIds()
    {
        if (!ClaimValueParser.TryParseCanonicalGuidClaims(
                User.FindAll(SharedClaimTypes.IdentityRoleId).Select(x => x.Value),
                32,
                out var roleIds))
        {
            throw new UnauthorizedAccessException("The immutable role membership claim set is invalid.");
        }
        return roleIds;
    }
}

[ApiController]
[Route("api/permissions/internal/permission-snapshots")]
[Authorize(Policy = PermissionsPolicies.CheckFeaturesInternal)]
public class InternalPermissionSnapshotsController : ControllerBase
{
    private readonly IPermissionSnapshotAppService _service;

    public InternalPermissionSnapshotsController(IPermissionSnapshotAppService service)
    {
        _service = service;
    }

    [HttpGet]
    public Task<PermissionSnapshotDto> GetAsync([FromQuery] Guid? tenantId, [FromQuery] Guid userId, [FromQuery] List<Guid> roleIds, CancellationToken cancellationToken)
    {
        return _service.GetInternalSnapshotAsync(tenantId, userId, roleIds, cancellationToken);
    }
}
