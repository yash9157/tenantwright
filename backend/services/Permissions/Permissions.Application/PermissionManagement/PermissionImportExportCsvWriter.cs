using Permissions.Application.Contracts.Definitions;
using System.Text;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.PermissionManagement;

public static class PermissionImportExportCsvWriter
{
    public static string WriteCatalog(IEnumerable<PermissionCatalogImportItemDto> nodes)
    {
        var builder = new StringBuilder();
        AppendRow(builder, "name", "displayName", "elementType", "elementKey", "parentName", "isEnabled", "allowsWildcard", "sortOrder");

        foreach (var node in nodes)
        {
            AppendRow(
                builder,
                node.Name,
                node.DisplayName,
                FormatElementType(node.ElementType),
                node.ElementKey,
                node.ParentName,
                FormatBoolean(node.IsEnabled),
                FormatBoolean(node.AllowsWildcard),
                node.SortOrder.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    public static string WriteProviderGrants(
        string providerName,
        string providerKey,
        IEnumerable<PermissionProviderGrantImportItemDto> grants)
    {
        var builder = new StringBuilder();
        AppendRow(builder, "providerName", "providerKey", "name", "isGranted", "effect");

        foreach (var grant in grants)
        {
            AppendRow(
                builder,
                providerName,
                providerKey,
                grant.Name,
                FormatBoolean(grant.IsGranted),
                grant.Effect.ToString());
        }

        return builder.ToString();
    }

    public static string WriteResourcePermissions(
        string resourceName,
        string resourceKey,
        string providerName,
        string providerKey,
        IEnumerable<PermissionResourceGrantImportItemDto> grants)
    {
        var builder = new StringBuilder();
        AppendRow(builder, "resourceName", "resourceKey", "providerName", "providerKey", "name", "isGranted", "action");

        foreach (var grant in grants)
        {
            AppendRow(
                builder,
                resourceName,
                resourceKey,
                providerName,
                providerKey,
                grant.Name,
                FormatBoolean(grant.IsGranted),
                grant.Action);
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, params string?[] values)
    {
        builder.AppendJoin(',', values.Select(Escape));
        builder.Append("\r\n");
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var mustQuote = value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal);
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return mustQuote ? $"\"{escaped}\"" : escaped;
    }

    private static string FormatBoolean(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatElementType(PermissionElementType value)
    {
        return Enum.IsDefined(value) ? value.ToString() : ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
