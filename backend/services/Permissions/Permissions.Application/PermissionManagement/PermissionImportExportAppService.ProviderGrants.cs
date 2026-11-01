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
    public async Task<PermissionProviderGrantExportDto> ExportProviderGrantsAsync(string providerName, string providerKey, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ExportPermissions, cancellationToken: cancellationToken);

        var normalizedProviderName = NormalizeAndEnsureSupportedProvider(providerName);
        var normalizedProviderKey = PermissionImportExportValidator.Normalize(providerKey);
        var tenantId = _currentTenant.Id;
        var grants = await _grantRepository.GetListAsync(
            x => x.TenantId == tenantId && x.ProviderName == normalizedProviderName && x.ProviderKey == normalizedProviderKey,
            includeDetails: false,
            cancellationToken: cancellationToken);

        return new PermissionProviderGrantExportDto
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            ProviderName = normalizedProviderName,
            ProviderKey = normalizedProviderKey,
            Grants = grants
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => new PermissionProviderGrantImportItemDto
                {
                    Name = x.Name,
                    IsGranted = true,
                    Effect = x.Effect
                })
                .ToList()
        };
    }

    public async Task<PermissionExportFileDto> ExportProviderGrantsFileAsync(
        string providerName,
        string providerKey,
        PermissionExportFormat format,
        CancellationToken cancellationToken = default)
    {
        var export = await ExportProviderGrantsAsync(providerName, providerKey, cancellationToken: cancellationToken);
        return ToExportFile(
            format,
            export.ExportedAtUtc,
            "permissions-provider-grants",
            export,
            () => PermissionImportExportCsvWriter.WriteProviderGrants(export.ProviderName, export.ProviderKey, export.Grants));
    }

    public async Task<PermissionImportValidationResultDto> ValidateProviderGrantImportAsync(
        string providerName,
        string providerKey,
        PermissionProviderGrantImportRequestDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ValidatePermissionImports, cancellationToken: cancellationToken);

        var knownPermissions = await LoadKnownPermissionNamesAsync(_currentTenant.Id, cancellationToken: cancellationToken);
        return PermissionImportExportValidator.ValidateProviderGrants(providerName, providerKey, input, knownPermissions);
    }

    public async Task<PermissionImportValidationResultDto> ValidateProviderGrantCsvImportAsync(
        string providerName,
        string providerKey,
        PermissionCsvImportRequestDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ValidatePermissionImports, cancellationToken: cancellationToken);

        var parsed = PermissionImportExportCsvParser.ParseProviderGrants(input.Content);
        var knownPermissions = await LoadKnownPermissionNamesAsync(_currentTenant.Id, cancellationToken: cancellationToken);
        var validation = PermissionImportExportValidator.ValidateProviderGrants(providerName, providerKey, parsed.Request, knownPermissions);
        AppendProviderCsvTargetIssues(validation, providerName, providerKey, parsed);
        return MergeParseIssues(validation, parsed.Issues);
    }

    [Audited]
    public async Task<PermissionImportApplyResultDto> ApplyProviderGrantImportAsync(
        string providerName,
        string providerKey,
        PermissionProviderGrantImportRequestDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ApplyPermissionImports, cancellationToken: cancellationToken);

        var tenantId = _currentTenant.Id;
        var knownPermissions = await LoadKnownPermissionNamesAsync(tenantId, cancellationToken: cancellationToken);
        var validation = PermissionImportExportValidator.ValidateProviderGrants(providerName, providerKey, input, knownPermissions);
        var result = ToApplyResult(validation);
        if (!validation.IsValid)
        {
            return result;
        }

        var normalizedProviderName = PermissionImportExportValidator.NormalizeProviderName(providerName);
        var normalizedProviderKey = PermissionImportExportValidator.Normalize(providerKey);
        var existingGrants = await _grantRepository.GetListAsync(
            x => x.TenantId == tenantId && x.ProviderName == normalizedProviderName && x.ProviderKey == normalizedProviderKey,
            includeDetails: false,
            cancellationToken: cancellationToken);
        var byName = existingGrants.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var hasChanges = false;

        // Flow 6: import must not become a back door around the approval workflow. Reject the batch
        // (fail-closed) if it would newly grant any approval-gated permission; those must go through
        // the request/approve flow, not a bulk import.
        var approvalRequired = new HashSet<string>(StringComparer.Ordinal);
        void CollectApprovalRequired(IEnumerable<Permissions.Application.Contracts.Definitions.PermissionDefinition> permissions)
        {
            foreach (var pd in permissions)
            {
                if (pd.RequiresApproval)
                {
                    approvalRequired.Add(pd.Name);
                }

                if (pd.Children.Count > 0)
                {
                    CollectApprovalRequired(pd.Children);
                }
            }
        }

        foreach (var definitionGroup in _definitionManager.GetGroups())
        {
            CollectApprovalRequired(definitionGroup.Permissions);
        }

        if (approvalRequired.Count > 0)
        {
            var flaggedInBatch = input.Grants
                .Where(g => g.IsGranted && g.Effect == Permissions.Domain.Shared.Enums.PermissionGrantEffect.Allow)
                .Select(g => PermissionImportExportValidator.Normalize(g.Name))
                .Where(name => approvalRequired.Contains(name) &&
                               (!byName.TryGetValue(name, out var current) || current.Effect != Permissions.Domain.Shared.Enums.PermissionGrantEffect.Allow))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (flaggedInBatch.Count > 0)
            {
                throw new Shared.SharedValidationException(
                    $"Import cannot grant approval-gated permissions ({string.Join(", ", flaggedInBatch)}); grant them through the approval workflow.");
            }
        }

        foreach (var item in input.Grants)
        {
            var permissionName = PermissionImportExportValidator.Normalize(item.Name);
            if (!item.IsGranted)
            {
                if (byName.TryGetValue(permissionName, out var toRemove))
                {
                    await _grantRepository.DeleteAsync(toRemove, autoSave: false, cancellationToken: cancellationToken);
                    result.RemovedCount++;
                    hasChanges = true;
                }
                else
                {
                    result.UnchangedCount++;
                }

                continue;
            }

            if (byName.TryGetValue(permissionName, out var existing))
            {
                if (existing.Effect != item.Effect)
                {
                    existing.SetEffect(item.Effect);
                    await _grantRepository.UpdateAsync(existing, autoSave: false, cancellationToken: cancellationToken);
                    result.UpdatedCount++;
                    hasChanges = true;
                }
                else
                {
                    result.UnchangedCount++;
                }

                continue;
            }

            await _grantRepository.InsertAsync(
                new PermissionGrant(
                    id: Guid.CreateVersion7(),
                    tenantId: tenantId,
                    name: permissionName,
                    providerName: normalizedProviderName,
                    providerKey: normalizedProviderKey,
                    effect: item.Effect,
                    creationTimeUtc: DateTime.UtcNow),
                autoSave: false,
                cancellationToken: cancellationToken);
            result.CreatedCount++;
            hasChanges = true;
        }

        if (hasChanges)
        {
            var stamp = await _stampService.GetNextStampAsync(tenantId, normalizedProviderName, normalizedProviderKey, cancellationToken: cancellationToken);
            await _grantCacheSync.SyncAsync(tenantId, normalizedProviderName, normalizedProviderKey, stamp, cancellationToken: cancellationToken);
            await _stampService.BumpAsync(tenantId, normalizedProviderName, normalizedProviderKey, stamp, cancellationToken: cancellationToken);
        }

        return result;
    }

    [Audited]
    public async Task<PermissionImportApplyResultDto> ApplyProviderGrantCsvImportAsync(
        string providerName,
        string providerKey,
        PermissionCsvImportRequestDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ApplyPermissionImports, cancellationToken: cancellationToken);

        var parsed = PermissionImportExportCsvParser.ParseProviderGrants(input.Content);
        var knownPermissions = await LoadKnownPermissionNamesAsync(_currentTenant.Id, cancellationToken: cancellationToken);
        var validation = PermissionImportExportValidator.ValidateProviderGrants(providerName, providerKey, parsed.Request, knownPermissions);
        AppendProviderCsvTargetIssues(validation, providerName, providerKey, parsed);
        validation = MergeParseIssues(validation, parsed.Issues);
        if (!validation.IsValid)
        {
            return ToApplyResult(validation);
        }

        return await ApplyProviderGrantImportAsync(providerName, providerKey, parsed.Request, cancellationToken: cancellationToken);
    }

}
