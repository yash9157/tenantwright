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
    public async Task<PermissionResourceGrantExportDto> ExportResourcePermissionsAsync(
        string resourceName,
        string resourceKey,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ExportPermissions, cancellationToken);

        var normalizedProviderName = NormalizeAndEnsureSupportedProvider(providerName);
        var normalizedProviderKey = PermissionImportExportValidator.Normalize(providerKey);
        var normalizedResourceName = PermissionImportExportValidator.Normalize(resourceName);
        var normalizedResourceKey = PermissionImportExportValidator.Normalize(resourceKey);
        await EnsureKnownResourceAsync(normalizedResourceName, normalizedResourceKey, cancellationToken);

        var tenantId = _currentTenant.Id;
        var grants = await _resourceGrantRepository.GetListAsync(
            x => x.TenantId == tenantId
                && x.ResourceName == normalizedResourceName
                && x.ResourceKey == normalizedResourceKey
                && x.ProviderName == normalizedProviderName
                && x.ProviderKey == normalizedProviderKey,
            includeDetails: false,
            cancellationToken: cancellationToken);

        return new PermissionResourceGrantExportDto
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            ResourceName = normalizedResourceName,
            ResourceKey = normalizedResourceKey,
            ProviderName = normalizedProviderName,
            ProviderKey = normalizedProviderKey,
            Grants = grants
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => new PermissionResourceGrantImportItemDto
                {
                    Action = "Upsert",
                    Name = x.Name,
                    IsGranted = true
                })
                .ToList()
        };
    }

    public async Task<PermissionExportFileDto> ExportResourcePermissionsFileAsync(
        string resourceName,
        string resourceKey,
        string providerName,
        string providerKey,
        PermissionExportFormat format,
        CancellationToken cancellationToken = default)
    {
        var export = await ExportResourcePermissionsAsync(resourceName, resourceKey, providerName, providerKey, cancellationToken);
        return ToExportFile(
            format,
            export.ExportedAtUtc,
            "permissions-resource-grants",
            export,
            () => PermissionImportExportCsvWriter.WriteResourcePermissions(
                export.ResourceName,
                export.ResourceKey,
                export.ProviderName,
                export.ProviderKey,
                export.Grants));
    }

    public async Task<PermissionImportValidationResultDto> ValidateResourcePermissionImportAsync(
        string resourceName,
        string resourceKey,
        string providerName,
        string providerKey,
        PermissionResourceGrantImportRequestDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ValidatePermissionImports, cancellationToken);

        var request = NormalizeResourceImportRequest(resourceName, resourceKey, input);
        var knownPermissions = await LoadKnownPermissionNamesAsync(_currentTenant.Id, cancellationToken);
        var knownResources = await LoadKnownResourceKeysAsync(_currentTenant.Id, cancellationToken);
        var validation = PermissionImportExportValidator.ValidateResourcePermissions(
            providerName,
            providerKey,
            request,
            knownPermissions,
            knownResources);
        AppendResourceImportPolicyIssues(validation, resourceName, resourceKey, providerName, input);
        return validation;
    }

    public async Task<PermissionImportValidationResultDto> ValidateResourcePermissionCsvImportAsync(
        string resourceName,
        string resourceKey,
        string providerName,
        string providerKey,
        PermissionCsvImportRequestDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ValidatePermissionImports, cancellationToken);

        var parsed = PermissionImportExportCsvParser.ParseResourcePermissions(input.Content);
        var request = NormalizeResourceImportRequest(resourceName, resourceKey, parsed.Request);
        var knownPermissions = await LoadKnownPermissionNamesAsync(_currentTenant.Id, cancellationToken);
        var knownResources = await LoadKnownResourceKeysAsync(_currentTenant.Id, cancellationToken);
        var validation = PermissionImportExportValidator.ValidateResourcePermissions(
            providerName,
            providerKey,
            request,
            knownPermissions,
            knownResources);
        AppendResourceImportPolicyIssues(validation, resourceName, resourceKey, providerName, parsed.Request);
        AppendResourceCsvTargetIssues(validation, resourceName, resourceKey, providerName, providerKey, parsed);
        return MergeParseIssues(validation, parsed.Issues);
    }

    [Audited]
    public async Task<PermissionImportApplyResultDto> ApplyResourcePermissionImportAsync(
        string resourceName,
        string resourceKey,
        string providerName,
        string providerKey,
        PermissionResourceGrantImportRequestDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ApplyPermissionImports, cancellationToken);

        var tenantId = _currentTenant.Id;
        var request = NormalizeResourceImportRequest(resourceName, resourceKey, input);
        var knownPermissions = await LoadKnownPermissionNamesAsync(tenantId, cancellationToken);
        var knownResources = await LoadKnownResourceKeysAsync(tenantId, cancellationToken);
        var validation = PermissionImportExportValidator.ValidateResourcePermissions(
            providerName,
            providerKey,
            request,
            knownPermissions,
            knownResources);
        AppendResourceImportPolicyIssues(validation, resourceName, resourceKey, providerName, input);
        var result = ToApplyResult(validation);
        if (!validation.IsValid)
        {
            return result;
        }

        var normalizedProviderName = PermissionImportExportValidator.NormalizeProviderName(providerName);
        var normalizedProviderKey = PermissionImportExportValidator.Normalize(providerKey);
        var existingGrants = await _resourceGrantRepository.GetListAsync(
            x => x.TenantId == tenantId
                && x.ResourceName == request.ResourceName
                && x.ResourceKey == request.ResourceKey
                && x.ProviderName == normalizedProviderName
                && x.ProviderKey == normalizedProviderKey,
            includeDetails: false,
            cancellationToken: cancellationToken);
        var byName = existingGrants.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var hasChanges = false;

        foreach (var item in request.Grants.Where(x => PermissionImportExportValidator.IsUpsert(x.Action)))
        {
            var permissionName = PermissionImportExportValidator.Normalize(item.Name);
            if (!item.IsGranted)
            {
                if (byName.TryGetValue(permissionName, out var toRemove))
                {
                    await _resourceGrantRepository.DeleteAsync(toRemove, autoSave: false, cancellationToken: cancellationToken);
                    result.RemovedCount++;
                    hasChanges = true;
                }
                else
                {
                    result.UnchangedCount++;
                }

                continue;
            }

            if (byName.ContainsKey(permissionName))
            {
                result.UnchangedCount++;
                continue;
            }

            await _resourceGrantRepository.InsertAsync(
                new ResourcePermissionGrant(
                    Guid.CreateVersion7(),
                    tenantId,
                    permissionName,
                    request.ResourceName,
                    request.ResourceKey,
                    1,
                    normalizedProviderName,
                    normalizedProviderKey,
                    PermissionGrantEffect.Allow,
                    DateTime.UtcNow,
                    expiresAtUtc: null,
                    reason: null,
                    ticketId: null,
                    grantedByUserId: null),
                autoSave: false,
                cancellationToken: cancellationToken);
            result.CreatedCount++;
            hasChanges = true;
        }

        if (hasChanges)
        {
            await _stampService.BumpAsync(tenantId, normalizedProviderName, normalizedProviderKey, cancellationToken: cancellationToken);
        }

        return result;
    }

    [Audited]
    public async Task<PermissionImportApplyResultDto> ApplyResourcePermissionCsvImportAsync(
        string resourceName,
        string resourceKey,
        string providerName,
        string providerKey,
        PermissionCsvImportRequestDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ApplyPermissionImports, cancellationToken);

        var parsed = PermissionImportExportCsvParser.ParseResourcePermissions(input.Content);
        var request = NormalizeResourceImportRequest(resourceName, resourceKey, parsed.Request);
        var knownPermissions = await LoadKnownPermissionNamesAsync(_currentTenant.Id, cancellationToken);
        var knownResources = await LoadKnownResourceKeysAsync(_currentTenant.Id, cancellationToken);
        var validation = PermissionImportExportValidator.ValidateResourcePermissions(
            providerName,
            providerKey,
            request,
            knownPermissions,
            knownResources);
        AppendResourceImportPolicyIssues(validation, resourceName, resourceKey, providerName, parsed.Request);
        AppendResourceCsvTargetIssues(validation, resourceName, resourceKey, providerName, providerKey, parsed);
        validation = MergeParseIssues(validation, parsed.Issues);
        if (!validation.IsValid)
        {
            return ToApplyResult(validation);
        }

        return await ApplyResourcePermissionImportAsync(resourceName, resourceKey, providerName, providerKey, parsed.Request, cancellationToken);
    }

}
