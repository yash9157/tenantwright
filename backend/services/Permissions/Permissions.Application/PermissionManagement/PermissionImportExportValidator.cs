using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.PermissionManagement;

public static class PermissionImportExportValidator
{
    private static readonly HashSet<PermissionElementType> ValidElementTypes = Enum
        .GetValues<PermissionElementType>()
        .ToHashSet();

    public static PermissionImportValidationResultDto ValidateCatalog(
        PermissionCatalogImportRequestDto input,
        IReadOnlySet<string> existingNames)
    {
        var result = new PermissionImportValidationResultDto
        {
            TotalCount = input.Nodes.Count
        };

        var importNames = new HashSet<string>(StringComparer.Ordinal);
        var duplicateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in input.Nodes)
        {
            var name = Normalize(item.Name);
            if (!string.IsNullOrWhiteSpace(name) && !importNames.Add(name))
            {
                duplicateNames.Add(name);
            }
        }

        foreach (var item in input.Nodes)
        {
            var name = Normalize(item.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                AddIssue(result, "RequiredName", "Catalog node name is required.", item.Name);
                continue;
            }

            if (!IsUpsert(item.Action))
            {
                AddIssue(result, "UnsupportedAction", "Only upsert imports are supported. Delete-by-omission and delete actions are not supported in v1.", name);
            }

            if (!ValidElementTypes.Contains(item.ElementType))
            {
                AddIssue(result, "InvalidElementType", $"Unsupported element type value '{(byte)item.ElementType}'.", name);
            }

            if (string.IsNullOrWhiteSpace(item.ElementKey))
            {
                AddIssue(result, "RequiredElementKey", "Catalog node element key is required.", name);
            }

            var parentName = Normalize(item.ParentName);
            if (!string.IsNullOrWhiteSpace(parentName)
                && !existingNames.Contains(parentName)
                && !importNames.Contains(parentName))
            {
                AddIssue(result, "MissingParent", $"Parent catalog node '{parentName}' was not found in the existing catalog or import payload.", name);
            }

            if (string.Equals(name, parentName, StringComparison.Ordinal))
            {
                AddIssue(result, "InvalidParent", "Catalog node parent cannot be the node itself.", name);
            }
        }

        foreach (var duplicateName in duplicateNames)
        {
            AddIssue(result, "DuplicateName", $"Catalog import contains duplicate node '{duplicateName}'.", duplicateName);
        }

        result.UpsertCount = input.Nodes.Count(x => IsUpsert(x.Action));
        result.IsValid = result.Issues.All(x => !string.Equals(x.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static PermissionImportValidationResultDto ValidateProviderGrants(
        string providerName,
        string providerKey,
        PermissionProviderGrantImportRequestDto input,
        IReadOnlySet<string> knownPermissionNames)
    {
        var result = new PermissionImportValidationResultDto
        {
            TotalCount = input.Grants.Count
        };

        var normalizedProviderName = NormalizeProviderName(providerName);
        if (!IsSupportedProvider(normalizedProviderName))
        {
            AddIssue(result, "UnsupportedProvider", "Provider grant import/export supports only Role and User providers in v1.", normalizedProviderName);
        }

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            AddIssue(result, "RequiredProviderKey", "Provider key is required.", normalizedProviderName);
        }
        else if (normalizedProviderName == PermissionProviderNames.Role &&
                 (!Guid.TryParseExact(providerKey.Trim(), "D", out var roleId) || roleId == Guid.Empty))
        {
            AddIssue(result, "InvalidRoleProviderKey", "Role provider keys must be canonical immutable role IDs.", providerKey);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in input.Grants)
        {
            var name = Normalize(grant.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                AddIssue(result, "RequiredName", "Permission name is required.", grant.Name);
                continue;
            }

            if (!seen.Add(name))
            {
                duplicateNames.Add(name);
            }

            if (!knownPermissionNames.Contains(name))
            {
                AddIssue(result, "UnknownPermission", $"Permission '{name}' is not defined in definitions or catalog.", name);
            }

            if (!Enum.IsDefined(grant.Effect))
            {
                AddIssue(result, "InvalidEffect", $"Unsupported grant effect value '{(byte)grant.Effect}'.", name);
            }
        }

        foreach (var duplicateName in duplicateNames)
        {
            AddIssue(result, "DuplicateName", $"Provider grant import contains duplicate permission '{duplicateName}'.", duplicateName);
        }

        result.UpsertCount = input.Grants.Count;
        result.IsValid = result.Issues.All(x => !string.Equals(x.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static PermissionImportValidationResultDto ValidateResourcePermissions(
        string providerName,
        string providerKey,
        PermissionResourceGrantImportRequestDto input,
        IReadOnlySet<string> knownPermissionNames,
        IReadOnlySet<string> knownResourceKeys)
    {
        var result = new PermissionImportValidationResultDto
        {
            TotalCount = input.Grants.Count
        };

        var normalizedProviderName = NormalizeProviderName(providerName);
        if (!IsSupportedProvider(normalizedProviderName))
        {
            AddIssue(result, "UnsupportedProvider", "Resource permission import/export supports only Role and User providers in v1.", normalizedProviderName);
        }

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            AddIssue(result, "RequiredProviderKey", "Provider key is required.", normalizedProviderName);
        }
        else if (normalizedProviderName == PermissionProviderNames.Role &&
                 (!Guid.TryParseExact(providerKey.Trim(), "D", out var roleId) || roleId == Guid.Empty))
        {
            AddIssue(result, "InvalidRoleProviderKey", "Role provider keys must be canonical immutable role IDs.", providerKey);
        }

        var resourceName = Normalize(input.ResourceName);
        var resourceKey = Normalize(input.ResourceKey);
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            AddIssue(result, "RequiredResourceName", "Resource name is required.", input.ResourceName);
        }

        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            AddIssue(result, "RequiredResourceKey", "Resource key is required.", input.ResourceKey);
        }

        var resourceIdentity = FormatResourceIdentity(resourceName, resourceKey);
        if (!string.IsNullOrWhiteSpace(resourceName)
            && !string.IsNullOrWhiteSpace(resourceKey)
            && !knownResourceKeys.Contains(resourceIdentity))
        {
            AddIssue(result, "UnknownResource", $"Resource '{resourceIdentity}' was not found in the permission catalog.", resourceIdentity);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in input.Grants)
        {
            var name = Normalize(grant.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                AddIssue(result, "RequiredName", "Permission name is required.", grant.Name);
                continue;
            }

            if (!seen.Add(name))
            {
                duplicateNames.Add(name);
            }

            if (!IsUpsert(grant.Action))
            {
                AddIssue(result, "UnsupportedAction", "Only explicit upsert grant-state imports are supported. Delete-by-omission and destructive sync are not supported in v1.", name);
            }

            if (!knownPermissionNames.Contains(name))
            {
                AddIssue(result, "UnknownPermission", $"Permission '{name}' is not defined in definitions or catalog.", name);
            }
        }

        foreach (var duplicateName in duplicateNames)
        {
            AddIssue(result, "DuplicateName", $"Resource permission import contains duplicate permission '{duplicateName}'.", duplicateName);
        }

        result.UpsertCount = input.Grants.Count(x => IsUpsert(x.Action));
        result.IsValid = result.Issues.All(x => !string.Equals(x.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static bool IsSupportedProvider(string providerName)
    {
        var normalized = NormalizeProviderName(providerName);
        return string.Equals(normalized, PermissionProviderNames.Role, StringComparison.Ordinal)
            || string.Equals(normalized, PermissionProviderNames.User, StringComparison.Ordinal);
    }

    public static string NormalizeProviderName(string providerName)
    {
        return PermissionProviderNameNormalizer.Normalize(providerName);
    }

    public static string Normalize(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    public static bool IsUpsert(string? action)
    {
        return string.IsNullOrWhiteSpace(action)
            || string.Equals(action.Trim(), "Upsert", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatResourceIdentity(string resourceName, string resourceKey)
    {
        return $"{Normalize(resourceName)}:{Normalize(resourceKey)}";
    }

    private static void AddIssue(PermissionImportValidationResultDto result, string code, string message, string? itemName)
    {
        result.Issues.Add(new PermissionImportValidationIssueDto
        {
            Severity = "Error",
            Code = code,
            Message = message,
            ItemName = string.IsNullOrWhiteSpace(itemName) ? null : itemName.Trim()
        });
    }
}
