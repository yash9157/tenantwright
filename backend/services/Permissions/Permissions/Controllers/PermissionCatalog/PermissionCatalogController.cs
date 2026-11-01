using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.PermissionCatalog;
using Shared.AuditLogs.Auditing;

namespace Permissions.Controllers.PermissionCatalog;

[ApiController]
[Route("api/permissions/permission-catalog")]
[Authorize(Roles = "admin,HostAdmin")]
public sealed class PermissionCatalogController : ControllerBase
{
    private readonly IPermissionCatalogAppService _service;

    public PermissionCatalogController(IPermissionCatalogAppService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = PermissionsPolicies.ReadPermissionCatalog)]
    public Task<GetPermissionCatalogResultDto> GetAsync(CancellationToken cancellationToken) =>
        _service.GetAsync(cancellationToken);

    [HttpPost]
    [Authorize(Policy = PermissionsPolicies.CreatePermissionCatalog)]
    [Audited]
    public Task<PermissionCatalogEntryDto> CreateAsync(
        [FromBody] CreatePermissionCatalogEntryDto input,
        CancellationToken cancellationToken) =>
        _service.CreateAsync(input, cancellationToken);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionsPolicies.UpdatePermissionCatalog)]
    [Audited]
    public async Task<ActionResult<PermissionCatalogEntryDto>> UpdateAsync(
        Guid id,
        [FromBody] UpdatePermissionCatalogEntryDto input,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _service.UpdateAsync(id, input, cancellationToken);
        }
        catch (PermissionCatalogConcurrencyException exception)
        {
            return Conflict(new { code = "permission_catalog_version_conflict", exception.CurrentVersion });
        }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionsPolicies.DeletePermissionCatalog)]
    [Audited]
    public async Task<IActionResult> DeleteAsync(
        Guid id,
        [FromQuery] long expectedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            await _service.DeleteAsync(id, expectedVersion, cancellationToken);
            return NoContent();
        }
        catch (PermissionCatalogConcurrencyException exception)
        {
            return Conflict(new { code = "permission_catalog_version_conflict", exception.CurrentVersion });
        }
    }
}
