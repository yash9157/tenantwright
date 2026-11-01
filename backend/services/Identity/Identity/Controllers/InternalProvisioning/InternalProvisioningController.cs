using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.AuditLogs.Auditing;
using Shared.AspNetCore.Host.Idempotency;
using Shared.MultiTenancy;

namespace Identity.Controllers.InternalProvisioning;

[ApiController]
[IntegrationService]
[Route("api/identity/internal/provisioning")]
[Authorize(Policy = "IdentityInternal")]
public class InternalProvisioningController : ControllerBase
{
    private readonly IInternalUserProvisioningAppService _provisioningAppService;
    private readonly ICurrentTenant _currentTenant;

    public InternalProvisioningController(
        IInternalUserProvisioningAppService provisioningAppService,
        ICurrentTenant currentTenant)
    {
        _provisioningAppService = provisioningAppService;
        _currentTenant = currentTenant;
    }

    [HttpPost("tenants/{tenantId:guid}/first-user")]
    [RequireIdempotency]
    public async Task<IActionResult> ProvisionFirstUserAsync(
        Guid tenantId,
        [FromBody] ProvisionFirstUserInput input,
        CancellationToken cancellationToken)
    {
        if (!_currentTenant.Id.HasValue || _currentTenant.Id.Value != tenantId)
        {
            return BadRequest(new
            {
                error = "invalid_tenant_context",
                error_description = "Tenant context does not match request route."
            });
        }

        var result = await _provisioningAppService.ProvisionFirstUserAsync(input, cancellationToken);
        if (result.Succeeded)
        {
            return Ok(result);
        }

        if (result.TenantAlreadyInitialized)
        {
            return Conflict(new
            {
                error = "tenant_already_initialized",
                error_description = result.Error ?? "Tenant already has users."
            });
        }

        return BadRequest(new
        {
            error = "first_user_provision_failed",
            error_description = result.Error ?? "First user provisioning failed."
        });
    }
}
