namespace Permissions.Application.Contracts.PermissionManagement;

public interface IPermissionImportExportAppService
{
    Task<PermissionCatalogExportDto> ExportCatalogAsync(CancellationToken cancellationToken = default);
    Task<PermissionExportFileDto> ExportCatalogFileAsync(PermissionExportFormat format, CancellationToken cancellationToken = default);
    Task<PermissionImportValidationResultDto> ValidateCatalogImportAsync(PermissionCatalogImportRequestDto input, CancellationToken cancellationToken = default);
    Task<PermissionImportApplyResultDto> ApplyCatalogImportAsync(PermissionCatalogImportRequestDto input, CancellationToken cancellationToken = default);
    Task<PermissionImportValidationResultDto> ValidateCatalogCsvImportAsync(PermissionCsvImportRequestDto input, CancellationToken cancellationToken = default);
    Task<PermissionImportApplyResultDto> ApplyCatalogCsvImportAsync(PermissionCsvImportRequestDto input, CancellationToken cancellationToken = default);
    Task<PermissionProviderGrantExportDto> ExportProviderGrantsAsync(string providerName, string providerKey, CancellationToken cancellationToken = default);
    Task<PermissionExportFileDto> ExportProviderGrantsFileAsync(string providerName, string providerKey, PermissionExportFormat format, CancellationToken cancellationToken = default);
    Task<PermissionImportValidationResultDto> ValidateProviderGrantImportAsync(string providerName, string providerKey, PermissionProviderGrantImportRequestDto input, CancellationToken cancellationToken = default);
    Task<PermissionImportApplyResultDto> ApplyProviderGrantImportAsync(string providerName, string providerKey, PermissionProviderGrantImportRequestDto input, CancellationToken cancellationToken = default);
    Task<PermissionImportValidationResultDto> ValidateProviderGrantCsvImportAsync(string providerName, string providerKey, PermissionCsvImportRequestDto input, CancellationToken cancellationToken = default);
    Task<PermissionImportApplyResultDto> ApplyProviderGrantCsvImportAsync(string providerName, string providerKey, PermissionCsvImportRequestDto input, CancellationToken cancellationToken = default);
    Task<PermissionResourceGrantExportDto> ExportResourcePermissionsAsync(string resourceName, string resourceKey, string providerName, string providerKey, CancellationToken cancellationToken = default);
    Task<PermissionExportFileDto> ExportResourcePermissionsFileAsync(string resourceName, string resourceKey, string providerName, string providerKey, PermissionExportFormat format, CancellationToken cancellationToken = default);
    Task<PermissionImportValidationResultDto> ValidateResourcePermissionImportAsync(string resourceName, string resourceKey, string providerName, string providerKey, PermissionResourceGrantImportRequestDto input, CancellationToken cancellationToken = default);
    Task<PermissionImportApplyResultDto> ApplyResourcePermissionImportAsync(string resourceName, string resourceKey, string providerName, string providerKey, PermissionResourceGrantImportRequestDto input, CancellationToken cancellationToken = default);
    Task<PermissionImportValidationResultDto> ValidateResourcePermissionCsvImportAsync(string resourceName, string resourceKey, string providerName, string providerKey, PermissionCsvImportRequestDto input, CancellationToken cancellationToken = default);
    Task<PermissionImportApplyResultDto> ApplyResourcePermissionCsvImportAsync(string resourceName, string resourceKey, string providerName, string providerKey, PermissionCsvImportRequestDto input, CancellationToken cancellationToken = default);
}
