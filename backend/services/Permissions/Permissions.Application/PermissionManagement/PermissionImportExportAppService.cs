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

public partial class PermissionImportExportAppService : IPermissionImportExportAppService, ITransientDependency
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IRequestAuthorizationService _authorization;
    private readonly ICurrentTenant _currentTenant;
    private readonly PermissionDefinitionManager _definitionManager;
    private readonly IRepository<PermissionNode, Guid> _catalogRepository;
    private readonly IRepository<PermissionGrant, Guid> _grantRepository;
    private readonly IRepository<ResourcePermissionGrant, Guid> _resourceGrantRepository;
    private readonly PermissionProviderGrantCacheSyncService _grantCacheSync;
    private readonly PermissionStampService _stampService;

    public PermissionImportExportAppService(
        IRequestAuthorizationService authorization,
        ICurrentTenant currentTenant,
        PermissionDefinitionManager definitionManager,
        IRepository<PermissionNode, Guid> catalogRepository,
        IRepository<PermissionGrant, Guid> grantRepository,
        IRepository<ResourcePermissionGrant, Guid> resourceGrantRepository,
        PermissionProviderGrantCacheSyncService grantCacheSync,
        PermissionStampService stampService)
    {
        _authorization = authorization;
        _currentTenant = currentTenant;
        _definitionManager = definitionManager;
        _catalogRepository = catalogRepository;
        _grantRepository = grantRepository;
        _resourceGrantRepository = resourceGrantRepository;
        _grantCacheSync = grantCacheSync;
        _stampService = stampService;
    }

    private async Task<List<PermissionNode>> LoadCatalogNodesAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        return await _catalogRepository.GetListAsync(
            x => x.TenantId == tenantId,
            includeDetails: false,
            cancellationToken: cancellationToken);
    }

    private async Task<HashSet<string>> LoadCatalogNamesAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        var nodes = await LoadCatalogNodesAsync(tenantId, cancellationToken);
        return nodes.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<HashSet<string>> LoadKnownPermissionNamesAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        var names = Flatten(_definitionManager.GetGroups().SelectMany(x => x.Permissions))
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);

        var catalogNames = await LoadCatalogNamesAsync(tenantId, cancellationToken);
        names.UnionWith(catalogNames);
        return names;
    }

    private async Task<HashSet<string>> LoadKnownResourceKeysAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        var nodes = await LoadCatalogNodesAsync(tenantId, cancellationToken);
        return nodes
            .Select(x => PermissionImportExportValidator.FormatResourceIdentity(x.ElementType.ToString(), x.ElementKey))
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task EnsureKnownResourceAsync(string resourceName, string resourceKey, CancellationToken cancellationToken)
    {
        var knownResources = await LoadKnownResourceKeysAsync(_currentTenant.Id, cancellationToken);
        var resourceIdentity = PermissionImportExportValidator.FormatResourceIdentity(resourceName, resourceKey);
        if (!knownResources.Contains(resourceIdentity))
        {
            throw new ArgumentException($"Resource '{resourceIdentity}' was not found in the permission catalog.", nameof(resourceKey));
        }
    }

    private static PermissionCatalogImportItemDto ToCatalogImportItem(PermissionNode node)
    {
        return new PermissionCatalogImportItemDto
        {
            Action = "Upsert",
            Name = node.Name,
            DisplayName = node.DisplayName,
            ElementType = node.ElementType,
            ElementKey = node.ElementKey,
            ParentName = node.ParentName,
            IsEnabled = node.IsEnabled,
            AllowsWildcard = node.AllowsWildcard,
            SortOrder = node.SortOrder
        };
    }

    private static bool ApplyCatalogMetadata(PermissionNode node, PermissionCatalogImportItemDto item)
    {
        var before = new
        {
            node.DisplayName,
            node.ParentName,
            node.IsEnabled,
            node.AllowsWildcard,
            node.SortOrder
        };

        node.SetDisplayName(item.DisplayName);
        node.SetParentName(PermissionImportExportValidator.Normalize(item.ParentName));
        if (item.IsEnabled)
        {
            node.Enable();
        }
        else
        {
            node.Disable();
        }
        node.SetWildcardAllowed(item.AllowsWildcard);
        node.SetSortOrder(item.SortOrder);

        return before.DisplayName != node.DisplayName
            || before.ParentName != node.ParentName
            || before.IsEnabled != node.IsEnabled
            || before.AllowsWildcard != node.AllowsWildcard
            || before.SortOrder != node.SortOrder;
    }

    private static PermissionImportApplyResultDto ToApplyResult(PermissionImportValidationResultDto validation)
    {
        return new PermissionImportApplyResultDto
        {
            IsValid = validation.IsValid,
            TotalCount = validation.TotalCount,
            Issues = validation.Issues,
            Warnings = validation.Warnings
        };
    }

    private static PermissionImportValidationResultDto MergeParseIssues(
        PermissionImportValidationResultDto validation,
        IEnumerable<PermissionImportValidationIssueDto> issues)
    {
        validation.Issues.AddRange(issues);
        validation.IsValid = validation.Issues.All(x => !string.Equals(x.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        return validation;
    }

    private static PermissionExportFileDto ToExportFile<TExport>(
        PermissionExportFormat format,
        DateTimeOffset exportedAtUtc,
        string fileNamePrefix,
        TExport export,
        Func<string> csvFactory)
    {
        return format switch
        {
            PermissionExportFormat.Csv => new PermissionExportFileDto
            {
                Content = csvFactory(),
                ContentType = "text/csv",
                ExportedAtUtc = exportedAtUtc,
                FileName = $"{fileNamePrefix}.csv",
                Format = PermissionExportFormat.Csv
            },
            PermissionExportFormat.Json => new PermissionExportFileDto
            {
                Content = JsonSerializer.Serialize(export, ExportJsonOptions),
                ContentType = "application/json",
                ExportedAtUtc = exportedAtUtc,
                FileName = $"{fileNamePrefix}.json",
                Format = PermissionExportFormat.Json
            },
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format.")
        };
    }

    private static PermissionResourceGrantImportRequestDto NormalizeResourceImportRequest(
        string resourceName,
        string resourceKey,
        PermissionResourceGrantImportRequestDto input)
    {
        var routeResourceName = PermissionImportExportValidator.Normalize(resourceName);
        var routeResourceKey = PermissionImportExportValidator.Normalize(resourceKey);

        return new PermissionResourceGrantImportRequestDto
        {
            ResourceName = routeResourceName,
            ResourceKey = routeResourceKey,
            Grants = input.Grants
        };
    }

    private void AppendResourceImportPolicyIssues(
        PermissionImportValidationResultDto validation,
        string resourceName,
        string resourceKey,
        string providerName,
        PermissionResourceGrantImportRequestDto input)
    {
        var routeResourceName = PermissionImportExportValidator.Normalize(resourceName);
        var routeResourceKey = PermissionImportExportValidator.Normalize(resourceKey);
        var bodyResourceName = PermissionImportExportValidator.Normalize(input.ResourceName);
        var bodyResourceKey = PermissionImportExportValidator.Normalize(input.ResourceKey);

        if (!string.IsNullOrWhiteSpace(bodyResourceName)
            && !string.Equals(bodyResourceName, routeResourceName, StringComparison.Ordinal))
        {
            AddValidationIssue(validation, "ResourceTargetMismatch", "Import payload resourceName must match the selected catalog resource.", bodyResourceName);
        }

        if (!string.IsNullOrWhiteSpace(bodyResourceKey)
            && !string.Equals(bodyResourceKey, routeResourceKey, StringComparison.Ordinal))
        {
            AddValidationIssue(validation, "ResourceTargetMismatch", "Import payload resourceKey must match the selected catalog resource.", bodyResourceKey);
        }

        var normalizedProviderName = PermissionImportExportValidator.NormalizeProviderName(providerName);
        var definitions = new Dictionary<string, PermissionDefinition>(StringComparer.Ordinal);
        foreach (var definition in Flatten(_definitionManager.GetGroups().SelectMany(x => x.Permissions)))
        {
            definitions.TryAdd(definition.Name, definition);
        }
        foreach (var grant in input.Grants)
        {
            var permissionName = PermissionImportExportValidator.Normalize(grant.Name);
            if (!definitions.TryGetValue(permissionName, out var definition))
            {
                continue;
            }

            if (!definition.IsEnabled)
            {
                AddValidationIssue(validation, "DisabledPermission", $"Permission '{permissionName}' is disabled.", permissionName);
            }

            if (PermissionPatternHelper.IsWildcard(permissionName) && !definition.AllowsWildcard)
            {
                AddValidationIssue(validation, "WildcardNotAllowed", $"Permission '{permissionName}' does not allow wildcard grants.", permissionName);
            }

            var allowedProviders = definition.Providers.Count == 0
                ? [PermissionProviderNames.Role, PermissionProviderNames.User]
                : definition.Providers.Select(PermissionImportExportValidator.NormalizeProviderName).Distinct(StringComparer.Ordinal).ToArray();
            if (!allowedProviders.Contains(normalizedProviderName, StringComparer.Ordinal))
            {
                AddValidationIssue(validation, "ProviderNotAllowed", $"Provider '{normalizedProviderName}' is not allowed for permission '{permissionName}'.", permissionName);
            }
        }
    }

    private static void AppendProviderCsvTargetIssues(
        PermissionImportValidationResultDto validation,
        string providerName,
        string providerKey,
        PermissionCsvParseResult<PermissionProviderGrantImportRequestDto> parsed)
    {
        AppendCsvTargetIssue(validation, "ProviderTargetMismatch", "CSV providerName must match the selected provider.", parsed.ProviderName, PermissionImportExportValidator.NormalizeProviderName(providerName));
        AppendCsvTargetIssue(validation, "ProviderTargetMismatch", "CSV providerKey must match the selected provider key.", parsed.ProviderKey, PermissionImportExportValidator.Normalize(providerKey));
    }

    private static void AppendResourceCsvTargetIssues(
        PermissionImportValidationResultDto validation,
        string resourceName,
        string resourceKey,
        string providerName,
        string providerKey,
        PermissionCsvParseResult<PermissionResourceGrantImportRequestDto> parsed)
    {
        AppendCsvTargetIssue(validation, "ResourceTargetMismatch", "CSV resourceName must match the selected catalog resource.", parsed.ResourceName, PermissionImportExportValidator.Normalize(resourceName));
        AppendCsvTargetIssue(validation, "ResourceTargetMismatch", "CSV resourceKey must match the selected catalog resource.", parsed.ResourceKey, PermissionImportExportValidator.Normalize(resourceKey));
        AppendCsvTargetIssue(validation, "ProviderTargetMismatch", "CSV providerName must match the selected provider.", parsed.ProviderName, PermissionImportExportValidator.NormalizeProviderName(providerName));
        AppendCsvTargetIssue(validation, "ProviderTargetMismatch", "CSV providerKey must match the selected provider key.", parsed.ProviderKey, PermissionImportExportValidator.Normalize(providerKey));
    }

    private static void AppendCsvTargetIssue(
        PermissionImportValidationResultDto validation,
        string code,
        string message,
        string? actual,
        string expected)
    {
        if (string.IsNullOrWhiteSpace(actual)
            || string.Equals(PermissionImportExportValidator.Normalize(actual), expected, StringComparison.Ordinal))
        {
            return;
        }

        AddValidationIssue(validation, code, message, actual);
    }

    private static void AddValidationIssue(
        PermissionImportValidationResultDto validation,
        string code,
        string message,
        string? itemName)
    {
        validation.Issues.Add(new PermissionImportValidationIssueDto
        {
            Severity = "Error",
            Code = code,
            Message = message,
            ItemName = string.IsNullOrWhiteSpace(itemName) ? null : itemName.Trim()
        });
        validation.IsValid = false;
    }

    private static string NormalizeAndEnsureSupportedProvider(string providerName)
    {
        var normalized = PermissionImportExportValidator.NormalizeProviderName(providerName);
        if (!PermissionImportExportValidator.IsSupportedProvider(normalized))
        {
            throw new ArgumentException("Provider grant import/export supports only Role and User providers in v1.", nameof(providerName));
        }

        return normalized;
    }

    private static IEnumerable<PermissionDefinition> Flatten(IEnumerable<PermissionDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            yield return definition;
            foreach (var child in Flatten(definition.Children))
            {
                yield return child;
            }
        }
    }
}
