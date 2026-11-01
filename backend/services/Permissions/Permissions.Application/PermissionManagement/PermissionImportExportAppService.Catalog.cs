using System.Text.Json;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Definitions;
using Permissions.Application.Evaluation;
using Permissions.Domain.Catalog;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.AuditLogs.Auditing;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Permissions.Application.PermissionManagement;

public partial class PermissionImportExportAppService
{
    public async Task<PermissionCatalogExportDto> ExportCatalogAsync(CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ExportPermissions, cancellationToken);

        var tenantId = _currentTenant.Id;
        var nodes = await LoadCatalogNodesAsync(tenantId, cancellationToken);

        return new PermissionCatalogExportDto
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Nodes = nodes
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .Select(ToCatalogImportItem)
                .ToList()
        };
    }

    public async Task<PermissionExportFileDto> ExportCatalogFileAsync(
        PermissionExportFormat format,
        CancellationToken cancellationToken = default)
    {
        var export = await ExportCatalogAsync(cancellationToken);
        return ToExportFile(
            format,
            export.ExportedAtUtc,
            "permissions-catalog",
            export,
            () => PermissionImportExportCsvWriter.WriteCatalog(export.Nodes));
    }

    public async Task<PermissionImportValidationResultDto> ValidateCatalogImportAsync(PermissionCatalogImportRequestDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ValidatePermissionImports, cancellationToken);

        var existingNames = await LoadCatalogNamesAsync(_currentTenant.Id, cancellationToken);
        return PermissionImportExportValidator.ValidateCatalog(input, existingNames);
    }

    public async Task<PermissionImportValidationResultDto> ValidateCatalogCsvImportAsync(
        PermissionCsvImportRequestDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ValidatePermissionImports, cancellationToken);

        var parsed = PermissionImportExportCsvParser.ParseCatalog(input.Content);
        var existingNames = await LoadCatalogNamesAsync(_currentTenant.Id, cancellationToken);
        return MergeParseIssues(
            PermissionImportExportValidator.ValidateCatalog(parsed.Request, existingNames),
            parsed.Issues);
    }

    [Audited]
    public async Task<PermissionImportApplyResultDto> ApplyCatalogImportAsync(PermissionCatalogImportRequestDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ApplyPermissionImports, cancellationToken);

        var tenantId = _currentTenant.Id;
        var existingNodes = await LoadCatalogNodesAsync(tenantId, cancellationToken);
        var existingNames = existingNodes.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var validation = PermissionImportExportValidator.ValidateCatalog(input, existingNames);
        var result = ToApplyResult(validation);
        if (!validation.IsValid)
        {
            return result;
        }

        var byName = existingNodes.ToDictionary(x => x.Name, StringComparer.Ordinal);
        foreach (var item in input.Nodes.Where(x => PermissionImportExportValidator.IsUpsert(x.Action)))
        {
            var name = PermissionImportExportValidator.Normalize(item.Name);
            if (byName.TryGetValue(name, out var existing))
            {
                var changed = ApplyCatalogMetadata(existing, item);
                if (changed)
                {
                    await _catalogRepository.UpdateAsync(existing, autoSave: false, cancellationToken: cancellationToken);
                    result.UpdatedCount++;
                }
                else
                {
                    result.UnchangedCount++;
                }

                continue;
            }

            await _catalogRepository.InsertAsync(
                new PermissionNode(
                    id: Guid.CreateVersion7(),
                    tenantId: tenantId,
                    name: name,
                    elementType: item.ElementType,
                    elementKey: item.ElementKey,
                    groupName: "Imported",
                    displayName: item.DisplayName,
                    parentName: PermissionImportExportValidator.Normalize(item.ParentName),
                    isEnabled: item.IsEnabled,
                    allowsWildcard: item.AllowsWildcard,
                    sortOrder: item.SortOrder),
                autoSave: false,
                cancellationToken: cancellationToken);
            result.CreatedCount++;
        }

        return result;
    }

    [Audited]
    public async Task<PermissionImportApplyResultDto> ApplyCatalogCsvImportAsync(
        PermissionCsvImportRequestDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ApplyPermissionImports, cancellationToken);

        var parsed = PermissionImportExportCsvParser.ParseCatalog(input.Content);
        var existingNames = await LoadCatalogNamesAsync(_currentTenant.Id, cancellationToken);
        var validation = MergeParseIssues(
            PermissionImportExportValidator.ValidateCatalog(parsed.Request, existingNames),
            parsed.Issues);
        if (!validation.IsValid)
        {
            return ToApplyResult(validation);
        }

        return await ApplyCatalogImportAsync(parsed.Request, cancellationToken);
    }

}
