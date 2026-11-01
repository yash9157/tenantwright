using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.AuditLogs.Auditing;
using Shared.AspNetCore.Host.Idempotency;
using Tenant.Application.Contracts.Tenants;
using Tenant.Application.Tenants;

namespace Tenant.Controllers.InternalTenants;

[ApiController]
[IntegrationService]
[Route("api/tenant/internal/tenants")]
public class InternalTenantsController : ControllerBase
{
    private readonly IInternalTenantQueryAppService _internalTenantQueries;
    private readonly ITenantAppService _tenantAppService;

    public InternalTenantsController(
        IInternalTenantQueryAppService internalTenantQueries,
        ITenantAppService tenantAppService)
    {
        _internalTenantQueries = internalTenantQueries;
        _tenantAppService = tenantAppService;
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "TenantInternal")]
    public async Task<ActionResult<TenantConfigurationDto?>> FindByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenant = await _internalTenantQueries.GetConfigurationByIdAsync(id, cancellationToken);
        return Ok(tenant);
    }

    [HttpGet("by-name/{normalizedName}")]
    [Authorize(Policy = "TenantInternal")]
    public async Task<ActionResult<TenantConfigurationDto?>> FindByNormalizedNameAsync(
        string normalizedName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return Ok(null);
        }

        var tenant = await _internalTenantQueries.GetConfigurationByNormalizedNameAsync(
            normalizedName,
            cancellationToken);
        return Ok(tenant);
    }

    [HttpPost]
    [Authorize(Policy = "TenantInternalProvisioning")]
    [RequireIdempotency]
    public async Task<ActionResult<TenantDto>> CreateAsync([FromBody] TenantCreateDto input)
    {
        try
        {
            var created = await _tenantAppService.CreateAsync(input);
            return Ok(created);
        }
        catch (TenantNameAlreadyExistsException ex)
        {
            return Conflict(new
            {
                error = "tenant_name_exists",
                error_description = ex.Message
            });
        }
    }

    [HttpDelete("{id:guid}/provisioning")]
    [Authorize(Policy = "TenantInternalProvisioning")]
    [RequireIdempotency]
    public async Task<IActionResult> DeleteProvisioningAsync(Guid id)
    {
        await _tenantAppService.DeleteProvisioningAsync(id);
        return Ok();
    }

    [HttpPost("serviceability/batch")]
    [Authorize(Policy = "TenantServiceabilityInternal")]
    public async Task<ActionResult<TenantServiceabilityBatchResult>> GetServiceabilityBatchAsync(
        [FromBody] TenantServiceabilityBatchRequest input,
        CancellationToken cancellationToken)
    {
        var tenantIds = input.TenantIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (tenantIds.Length == 0 || tenantIds.Length > 200)
        {
            return BadRequest("Between 1 and 200 distinct tenant ids are required.");
        }

        var result = await _internalTenantQueries.GetServiceabilityBatchAsync(
            tenantIds,
            cancellationToken);
        return Ok(result);
    }

    [HttpPost("public-registrations")]
    [Authorize(Policy = "TenantInternalProvisioning")]
    [RequireIdempotency]
    [Audited(Category = "tenant.public-registration")]
    public Task<PublicTenantRegistrationInitiateResult> InitiatePublicRegistrationAsync(
        [FromBody] PublicTenantRegistrationInitiateInput input)
        => _tenantAppService.InitiatePublicRegistrationAsync(input);

    [HttpGet("public-registrations/{operationId:guid}")]
    [Authorize(Policy = "TenantInternalProvisioning")]
    public Task<TenantProvisioningOperationDto?> GetPublicRegistrationAsync(Guid operationId)
        => _tenantAppService.GetPublicRegistrationAsync(operationId);

    [HttpPost("public-registrations/{operationId:guid}/retry")]
    [Authorize(Policy = "TenantInternalProvisioning")]
    [RequireIdempotency]
    [Audited(Category = "tenant.public-registration")]
    public Task<TenantProvisioningOperationDto> RetryPublicRegistrationAsync(Guid operationId)
        => _tenantAppService.RetryPublicRegistrationAsync(operationId);
}
