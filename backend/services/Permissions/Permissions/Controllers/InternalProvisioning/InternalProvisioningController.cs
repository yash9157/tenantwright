using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Shared.AspNetCore.Host.Idempotency;
using Shared.AuditLogs.Auditing;

namespace Permissions.Controllers.InternalProvisioning;

[ApiController]
[IntegrationService]
[Route("api/permissions/internal/provisioning")]
[Authorize(Policy = "PermissionsInternalProvisioning")]
public class InternalProvisioningController : ControllerBase
{
    private readonly ITenantAdminGrantProvisioningService _tenantAdminGrantProvisioningService;

    public InternalProvisioningController(ITenantAdminGrantProvisioningService tenantAdminGrantProvisioningService)
    {
        _tenantAdminGrantProvisioningService = tenantAdminGrantProvisioningService;
    }

    [HttpPost("tenants/{tenantId:guid}/roles/tenant-admin/grants")]
    [RequireIdempotency]
    public async Task<IActionResult> UpsertTenantAdminGrantsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            return BadRequest("Tenant id is required.");
        }

        await _tenantAdminGrantProvisioningService.UpsertTenantAdminGrantsAsync(tenantId, cancellationToken);
        return Ok();
    }

    [HttpDelete("tenants/{tenantId:guid}/roles/tenant-admin/grants")]
    [RequireIdempotency]
    public async Task<IActionResult> DeleteTenantAdminGrantsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            return BadRequest("Tenant id is required.");
        }

        await _tenantAdminGrantProvisioningService.DeleteTenantAdminGrantsAsync(
            tenantId,
            cancellationToken);
        return Ok();
    }
}
