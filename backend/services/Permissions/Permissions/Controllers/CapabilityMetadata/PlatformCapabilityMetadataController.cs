using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.CapabilityMetadata;
using Shared.AuditLogs.Auditing;
using Shared.MultiTenancy;

namespace Permissions.Controllers.CapabilityMetadata;

[ApiController]
[Route("api/permissions/capabilities")]
public sealed class PlatformCapabilityMetadataController : ControllerBase
{
    private const string PlatformAccessRoles = "admin,HostAdmin";

    private readonly ICapabilityMetadataAppService _service;
    private readonly ICurrentTenant _currentTenant;

    public PlatformCapabilityMetadataController(
        ICapabilityMetadataAppService service,
        ICurrentTenant currentTenant)
    {
        _service = service;
        _currentTenant = currentTenant;
    }

    [HttpGet]
    [Authorize(Policy = PermissionsPolicies.ReadGrantHistory, Roles = PlatformAccessRoles)]
    public async Task<ActionResult<CapabilityMetadataResultDto>> GetAsync(CancellationToken cancellationToken)
    {
        if (_currentTenant.IsAvailable)
        {
            return Forbid();
        }

        return Ok(await _service.GetAsync(cancellationToken));
    }
}

[ApiController]
[IntegrationService]
[Route("api/permissions/internal/capabilities")]
[Authorize(Policy = PermissionsPolicies.ReadCapabilityMetadataInternal)]
public sealed class InternalCapabilityMetadataController(ICapabilityMetadataAppService service) : ControllerBase
{
    [HttpPost("validate-features")]
    [ProducesResponseType<ValidateFeatureCapabilitiesResultDto>(StatusCodes.Status200OK)]
    public Task<ValidateFeatureCapabilitiesResultDto> ValidateFeaturesAsync(
        ValidateFeatureCapabilitiesRequestDto request,
        CancellationToken cancellationToken) =>
        service.ValidateFeaturesAsync(request, cancellationToken);
}
