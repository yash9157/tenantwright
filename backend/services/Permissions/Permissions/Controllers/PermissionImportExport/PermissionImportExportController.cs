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

namespace Permissions.Controllers.PermissionImportExport;

[ApiController]
[Route("api/permissions/import-export")]
public class PermissionImportExportController : ControllerBase
{
    private readonly IPermissionImportExportAppService _service;

    public PermissionImportExportController(IPermissionImportExportAppService service)
    {
        _service = service;
    }

    [HttpGet("catalog")]
    [Authorize(Policy = PermissionsPolicies.ExportPermissions)]
    public Task<PermissionCatalogExportDto> ExportCatalogAsync(CancellationToken cancellationToken)
    {
        return _service.ExportCatalogAsync(cancellationToken);
    }

    [HttpGet("catalog/file")]
    [Authorize(Policy = PermissionsPolicies.ExportPermissions)]
    public Task<PermissionExportFileDto> ExportCatalogFileAsync([FromQuery] PermissionExportFormat format, CancellationToken cancellationToken)
    {
        return _service.ExportCatalogFileAsync(format, cancellationToken);
    }

    [HttpPost("catalog/validate")]
    [Authorize(Policy = PermissionsPolicies.ValidatePermissionImports)]
    public Task<PermissionImportValidationResultDto> ValidateCatalogImportAsync([FromBody] PermissionCatalogImportRequestDto input, CancellationToken cancellationToken)
    {
        return _service.ValidateCatalogImportAsync(input, cancellationToken);
    }

    [HttpPost("catalog/csv/validate")]
    [Authorize(Policy = PermissionsPolicies.ValidatePermissionImports)]
    public Task<PermissionImportValidationResultDto> ValidateCatalogCsvImportAsync([FromBody] PermissionCsvImportRequestDto input, CancellationToken cancellationToken)
    {
        return _service.ValidateCatalogCsvImportAsync(input, cancellationToken);
    }

    [HttpGet("provider-grants/{providerName}/{providerKey}")]
    [Authorize(Policy = PermissionsPolicies.ExportPermissions)]
    public Task<PermissionProviderGrantExportDto> ExportProviderGrantsAsync(string providerName, string providerKey, CancellationToken cancellationToken)
    {
        return _service.ExportProviderGrantsAsync(providerName, providerKey, cancellationToken);
    }

    [HttpGet("provider-grants/{providerName}/{providerKey}/file")]
    [Authorize(Policy = PermissionsPolicies.ExportPermissions)]
    public Task<PermissionExportFileDto> ExportProviderGrantsFileAsync(
        string providerName,
        string providerKey,
        [FromQuery] PermissionExportFormat format,
        CancellationToken cancellationToken)
    {
        return _service.ExportProviderGrantsFileAsync(providerName, providerKey, format, cancellationToken);
    }

    [HttpPost("provider-grants/{providerName}/{providerKey}/validate")]
    [Authorize(Policy = PermissionsPolicies.ValidatePermissionImports)]
    public Task<PermissionImportValidationResultDto> ValidateProviderGrantImportAsync(
        string providerName,
        string providerKey,
        [FromBody] PermissionProviderGrantImportRequestDto input,
        CancellationToken cancellationToken)
    {
        return _service.ValidateProviderGrantImportAsync(providerName, providerKey, input, cancellationToken);
    }

    [HttpPost("provider-grants/{providerName}/{providerKey}/csv/validate")]
    [Authorize(Policy = PermissionsPolicies.ValidatePermissionImports)]
    public Task<PermissionImportValidationResultDto> ValidateProviderGrantCsvImportAsync(
        string providerName,
        string providerKey,
        [FromBody] PermissionCsvImportRequestDto input,
        CancellationToken cancellationToken)
    {
        return _service.ValidateProviderGrantCsvImportAsync(providerName, providerKey, input, cancellationToken);
    }

    [HttpGet("resource-permissions/providers/{providerName}/{providerKey}")]
    [Authorize(Policy = PermissionsPolicies.ExportPermissions)]
    public Task<PermissionResourceGrantExportDto> ExportResourcePermissionsAsync(
        [FromQuery] string resourceName,
        [FromQuery] string resourceKey,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken)
    {
        return _service.ExportResourcePermissionsAsync(resourceName, resourceKey, providerName, providerKey, cancellationToken);
    }

    [HttpGet("resource-permissions/providers/{providerName}/{providerKey}/file")]
    [Authorize(Policy = PermissionsPolicies.ExportPermissions)]
    public Task<PermissionExportFileDto> ExportResourcePermissionsFileAsync(
        [FromQuery] string resourceName,
        [FromQuery] string resourceKey,
        string providerName,
        string providerKey,
        [FromQuery] PermissionExportFormat format,
        CancellationToken cancellationToken)
    {
        return _service.ExportResourcePermissionsFileAsync(resourceName, resourceKey, providerName, providerKey, format, cancellationToken);
    }

    [HttpPost("resource-permissions/providers/{providerName}/{providerKey}/validate")]
    [Authorize(Policy = PermissionsPolicies.ValidatePermissionImports)]
    public Task<PermissionImportValidationResultDto> ValidateResourcePermissionImportAsync(
        [FromQuery] string resourceName,
        [FromQuery] string resourceKey,
        string providerName,
        string providerKey,
        [FromBody] PermissionResourceGrantImportRequestDto input,
        CancellationToken cancellationToken)
    {
        return _service.ValidateResourcePermissionImportAsync(resourceName, resourceKey, providerName, providerKey, input, cancellationToken);
    }

    [HttpPost("resource-permissions/providers/{providerName}/{providerKey}/csv/validate")]
    [Authorize(Policy = PermissionsPolicies.ValidatePermissionImports)]
    public Task<PermissionImportValidationResultDto> ValidateResourcePermissionCsvImportAsync(
        [FromQuery] string resourceName,
        [FromQuery] string resourceKey,
        string providerName,
        string providerKey,
        [FromBody] PermissionCsvImportRequestDto input,
        CancellationToken cancellationToken)
    {
        return _service.ValidateResourcePermissionCsvImportAsync(resourceName, resourceKey, providerName, providerKey, input, cancellationToken);
    }

}
