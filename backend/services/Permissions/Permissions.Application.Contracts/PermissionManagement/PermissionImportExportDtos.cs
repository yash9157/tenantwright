using Permissions.Application.Contracts.Definitions;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.PermissionManagement;

public enum PermissionExportFormat
{
    Json = 0,
    Csv = 1
}

public class PermissionExportFileDto
{
    public PermissionExportFormat Format { get; set; }
    public DateTimeOffset ExportedAtUtc { get; set; }
    public string FileName { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public string Content { get; set; } = default!;
}

public class PermissionCsvImportRequestDto
{
    public string Content { get; set; } = default!;
}

public class PermissionCatalogExportDto
{
    public string FormatVersion { get; set; } = "permissions.catalog.v1";
    public DateTimeOffset ExportedAtUtc { get; set; }
    public List<PermissionCatalogImportItemDto> Nodes { get; set; } = new();
}

public class PermissionCatalogImportRequestDto
{
    public List<PermissionCatalogImportItemDto> Nodes { get; set; } = new();
}

public class PermissionCatalogImportItemDto
{
    public string Action { get; set; } = "Upsert";
    public string Name { get; set; } = default!;
    public string? DisplayName { get; set; }
    public PermissionElementType ElementType { get; set; }
    public string ElementKey { get; set; } = default!;
    public string? ParentName { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool AllowsWildcard { get; set; } = true;
    public int SortOrder { get; set; }
}

public class PermissionProviderGrantExportDto
{
    public string FormatVersion { get; set; } = "permissions.provider-grants.v1";
    public DateTimeOffset ExportedAtUtc { get; set; }
    public string ProviderName { get; set; } = default!;
    public string ProviderKey { get; set; } = default!;
    public List<PermissionProviderGrantImportItemDto> Grants { get; set; } = new();
}

public class PermissionProviderGrantImportRequestDto
{
    public List<PermissionProviderGrantImportItemDto> Grants { get; set; } = new();
}

public class PermissionProviderGrantImportItemDto
{
    public string Name { get; set; } = default!;
    public bool IsGranted { get; set; }
    public PermissionGrantEffect Effect { get; set; } = PermissionGrantEffect.Allow;
}

public class PermissionResourceGrantExportDto
{
    public string FormatVersion { get; set; } = "permissions.resource-grants.v1";
    public DateTimeOffset ExportedAtUtc { get; set; }
    public string ResourceName { get; set; } = default!;
    public string ResourceKey { get; set; } = default!;
    public string ProviderName { get; set; } = default!;
    public string ProviderKey { get; set; } = default!;
    public List<PermissionResourceGrantImportItemDto> Grants { get; set; } = new();
}

public class PermissionResourceGrantImportRequestDto
{
    public string ResourceName { get; set; } = default!;
    public string ResourceKey { get; set; } = default!;
    public List<PermissionResourceGrantImportItemDto> Grants { get; set; } = new();
}

public class PermissionResourceGrantImportItemDto
{
    public string Action { get; set; } = "Upsert";
    public string Name { get; set; } = default!;
    public bool IsGranted { get; set; }
}

public class PermissionImportValidationResultDto
{
    public bool IsValid { get; set; }
    public int TotalCount { get; set; }
    public int UpsertCount { get; set; }
    public List<PermissionImportValidationIssueDto> Issues { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class PermissionImportApplyResultDto
{
    public bool IsValid { get; set; }
    public int TotalCount { get; set; }
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int RemovedCount { get; set; }
    public int UnchangedCount { get; set; }
    public List<PermissionImportValidationIssueDto> Issues { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class PermissionImportValidationIssueDto
{
    public string Severity { get; set; } = "Error";
    public string Code { get; set; } = default!;
    public string Message { get; set; } = default!;
    public string? ItemName { get; set; }
}
