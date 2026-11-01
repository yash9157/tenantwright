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

public sealed class PermissionCsvParseResult<TRequest>
{
    public TRequest Request { get; }
    public List<PermissionImportValidationIssueDto> Issues { get; } = new();
    public string? ProviderName { get; set; }
    public string? ProviderKey { get; set; }
    public string? ResourceName { get; set; }
    public string? ResourceKey { get; set; }

    public PermissionCsvParseResult(TRequest request)
    {
        Request = request;
    }
}

public static class PermissionImportExportCsvParser
{
    private static readonly string[] CatalogColumns =
    [
        "name",
        "displayName",
        "elementType",
        "elementKey",
        "parentName",
        "isEnabled",
        "allowsWildcard",
        "sortOrder"
    ];

    private static readonly string[] ProviderGrantColumns =
    [
        "providerName",
        "providerKey",
        "name",
        "isGranted",
        "effect"
    ];

    private static readonly string[] ResourcePermissionColumns =
    [
        "resourceName",
        "resourceKey",
        "providerName",
        "providerKey",
        "name",
        "isGranted",
        "action"
    ];

    public static PermissionCsvParseResult<PermissionCatalogImportRequestDto> ParseCatalog(string? content)
    {
        var result = new PermissionCsvParseResult<PermissionCatalogImportRequestDto>(new PermissionCatalogImportRequestDto());
        var rows = ParseRows(content, result.Issues);
        if (rows.Count == 0)
        {
            return result;
        }

        var header = BuildHeader(rows[0], CatalogColumns, result.Issues);
        for (var index = 1; index < rows.Count; index++)
        {
            var rowNumber = index + 1;
            var row = rows[index];
            if (IsBlank(row))
            {
                continue;
            }

            if (!ValidateRowShape(row, rows[0].Count, rowNumber, result.Issues))
            {
                continue;
            }

            var name = Get(row, header, "name");
            var item = new PermissionCatalogImportItemDto
            {
                Action = "Upsert",
                Name = name,
                DisplayName = Get(row, header, "displayName"),
                ElementKey = Get(row, header, "elementKey"),
                ParentName = Get(row, header, "parentName")
            };

            var elementTypeValue = Get(row, header, "elementType");
            if (Enum.TryParse<PermissionElementType>(elementTypeValue, ignoreCase: true, out var elementType))
            {
                item.ElementType = elementType;
            }
            else
            {
                AddIssue(result.Issues, "InvalidElementType", $"Unsupported catalog element type '{elementTypeValue}'.", name);
            }

            if (TryParseBoolean(Get(row, header, "isEnabled"), "isEnabled", name, result.Issues, out var isEnabled))
            {
                item.IsEnabled = isEnabled;
            }

            if (TryParseBoolean(Get(row, header, "allowsWildcard"), "allowsWildcard", name, result.Issues, out var allowsWildcard))
            {
                item.AllowsWildcard = allowsWildcard;
            }

            var sortOrderValue = Get(row, header, "sortOrder");
            if (int.TryParse(sortOrderValue, out var sortOrder))
            {
                item.SortOrder = sortOrder;
            }
            else
            {
                AddIssue(result.Issues, "InvalidNumber", $"Column 'sortOrder' must be an integer value.", name);
            }

            result.Request.Nodes.Add(item);
        }

        return result;
    }

    public static PermissionCsvParseResult<PermissionProviderGrantImportRequestDto> ParseProviderGrants(string? content)
    {
        var result = new PermissionCsvParseResult<PermissionProviderGrantImportRequestDto>(new PermissionProviderGrantImportRequestDto());
        var rows = ParseRows(content, result.Issues);
        if (rows.Count == 0)
        {
            return result;
        }

        var header = BuildHeader(rows[0], ProviderGrantColumns, result.Issues);
        for (var index = 1; index < rows.Count; index++)
        {
            var rowNumber = index + 1;
            var row = rows[index];
            if (IsBlank(row))
            {
                continue;
            }

            if (!ValidateRowShape(row, rows[0].Count, rowNumber, result.Issues))
            {
                continue;
            }

            var name = Get(row, header, "name");
            result.ProviderName = FirstNonEmpty(result.ProviderName, Get(row, header, "providerName"));
            result.ProviderKey = FirstNonEmpty(result.ProviderKey, Get(row, header, "providerKey"));
            var item = new PermissionProviderGrantImportItemDto
            {
                Name = name
            };

            if (TryParseBoolean(Get(row, header, "isGranted"), "isGranted", name, result.Issues, out var isGranted))
            {
                item.IsGranted = isGranted;
            }

            var effectValue = Get(row, header, "effect");
            if (Enum.TryParse<PermissionGrantEffect>(effectValue, ignoreCase: true, out var effect))
            {
                item.Effect = effect;
            }
            else
            {
                AddIssue(result.Issues, "InvalidEffect", $"Unsupported provider grant effect '{effectValue}'.", name);
            }

            result.Request.Grants.Add(item);
        }

        return result;
    }

    public static PermissionCsvParseResult<PermissionResourceGrantImportRequestDto> ParseResourcePermissions(string? content)
    {
        var result = new PermissionCsvParseResult<PermissionResourceGrantImportRequestDto>(new PermissionResourceGrantImportRequestDto());
        var rows = ParseRows(content, result.Issues);
        if (rows.Count == 0)
        {
            return result;
        }

        var header = BuildHeader(rows[0], ResourcePermissionColumns, result.Issues);
        for (var index = 1; index < rows.Count; index++)
        {
            var rowNumber = index + 1;
            var row = rows[index];
            if (IsBlank(row))
            {
                continue;
            }

            if (!ValidateRowShape(row, rows[0].Count, rowNumber, result.Issues))
            {
                continue;
            }

            result.Request.ResourceName = FirstNonEmpty(result.Request.ResourceName, Get(row, header, "resourceName"));
            result.Request.ResourceKey = FirstNonEmpty(result.Request.ResourceKey, Get(row, header, "resourceKey"));
            result.ResourceName = FirstNonEmpty(result.ResourceName, Get(row, header, "resourceName"));
            result.ResourceKey = FirstNonEmpty(result.ResourceKey, Get(row, header, "resourceKey"));
            result.ProviderName = FirstNonEmpty(result.ProviderName, Get(row, header, "providerName"));
            result.ProviderKey = FirstNonEmpty(result.ProviderKey, Get(row, header, "providerKey"));

            var name = Get(row, header, "name");
            var item = new PermissionResourceGrantImportItemDto
            {
                Action = FirstNonEmpty(Get(row, header, "action"), "Upsert"),
                Name = name
            };

            if (TryParseBoolean(Get(row, header, "isGranted"), "isGranted", name, result.Issues, out var isGranted))
            {
                item.IsGranted = isGranted;
            }

            if (!PermissionImportExportValidator.IsUpsert(item.Action))
            {
                AddIssue(result.Issues, "UnsupportedAction", "Only explicit upsert grant-state imports are supported. Delete-by-omission and destructive sync are not supported in v1.", name);
            }

            result.Request.Grants.Add(item);
        }

        return result;
    }

    private static Dictionary<string, int> BuildHeader(
        List<string> row,
        IReadOnlyCollection<string> expectedColumns,
        List<PermissionImportValidationIssueDto> issues)
    {
        var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < row.Count; index++)
        {
            var column = row[index].Trim();
            if (string.IsNullOrWhiteSpace(column))
            {
                AddIssue(issues, "UnsupportedColumn", "Blank CSV columns are not supported.", $"column {index + 1}");
                continue;
            }

            if (!expectedColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                AddIssue(issues, "UnsupportedColumn", $"CSV column '{column}' is not supported for this import target.", column);
            }

            header.TryAdd(column, index);
        }

        foreach (var column in expectedColumns)
        {
            if (!header.ContainsKey(column))
            {
                AddIssue(issues, "MissingColumn", $"CSV column '{column}' is required for this import target.", column);
            }
        }

        return header;
    }

    private static List<List<string>> ParseRows(string? content, List<PermissionImportValidationIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            AddIssue(issues, "RequiredContent", "CSV content is required.", null);
            return [];
        }

        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < content.Length; index++)
        {
            var current = content[index];
            if (current == '"')
            {
                if (inQuotes && index + 1 < content.Length && content[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && current == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
                continue;
            }

            if (!inQuotes && (current == '\r' || current == '\n'))
            {
                if (current == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
                {
                    index++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
                continue;
            }

            cell.Append(current);
        }

        row.Add(cell.ToString());
        if (!IsBlank(row))
        {
            rows.Add(row);
        }

        if (inQuotes)
        {
            AddIssue(issues, "MalformedCsv", "CSV contains an unterminated quoted field.", null);
        }

        return rows;
    }

    private static bool ValidateRowShape(
        List<string> row,
        int expectedCount,
        int rowNumber,
        List<PermissionImportValidationIssueDto> issues)
    {
        if (row.Count == expectedCount)
        {
            return true;
        }

        AddIssue(issues, "MalformedRow", $"CSV row {rowNumber} has {row.Count} columns, but {expectedCount} were expected.", $"row {rowNumber}");
        return false;
    }

    private static string Get(List<string> row, Dictionary<string, int> header, string column)
    {
        return header.TryGetValue(column, out var index) && index >= 0 && index < row.Count
            ? row[index].Trim()
            : string.Empty;
    }

    private static bool TryParseBoolean(
        string value,
        string column,
        string? itemName,
        List<PermissionImportValidationIssueDto> issues,
        out bool parsed)
    {
        if (bool.TryParse(value, out parsed))
        {
            return true;
        }

        AddIssue(issues, "InvalidBoolean", $"Column '{column}' must be true or false.", itemName);
        return false;
    }

    private static bool IsBlank(List<string> row)
    {
        return row.Count == 0 || row.All(string.IsNullOrWhiteSpace);
    }

    private static string FirstNonEmpty(string? primary, string? fallback)
    {
        return string.IsNullOrWhiteSpace(primary) ? PermissionImportExportValidator.Normalize(fallback) : PermissionImportExportValidator.Normalize(primary);
    }

    private static void AddIssue(
        List<PermissionImportValidationIssueDto> issues,
        string code,
        string message,
        string? itemName)
    {
        issues.Add(new PermissionImportValidationIssueDto
        {
            Severity = "Error",
            Code = code,
            Message = message,
            ItemName = string.IsNullOrWhiteSpace(itemName) ? null : itemName.Trim()
        });
    }
}
