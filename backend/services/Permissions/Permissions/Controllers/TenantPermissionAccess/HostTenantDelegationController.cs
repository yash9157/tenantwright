using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Shared.AuditLogs.Auditing;

namespace Permissions.Controllers.TenantPermissionAccess;

[ApiController]
[Route("api/permissions/platform/tenants/{tenantId:guid}/delegation")]
[Authorize(Roles = "admin,HostAdmin", Policy = PermissionsPolicies.ManagePermissionGrants)]
public sealed class HostTenantDelegationController(ITenantDelegationAppService service) : ControllerBase
{
    [HttpGet]
    public Task<HostTenantDelegationCeilingDto> GetAsync(
        Guid tenantId,
        CancellationToken cancellationToken) => service.GetHostCeilingAsync(tenantId, cancellationToken);

    [HttpPut]
    [Audited(Category = "permissions.tenant-delegation.host")]
    public Task<HostTenantDelegationCeilingUpdateResult> UpdateAsync(
        Guid tenantId,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        [FromBody] UpdateHostTenantDelegationCeilingInput input,
        CancellationToken cancellationToken) => service.UpdateHostCeilingAsync(tenantId, idempotencyKey, input, cancellationToken);
}
