using System.Text.Json.Serialization;

namespace Identity.Application.Contracts.Users;

public sealed class UserExportFilterSnapshot
{
    public string? Filter { get; set; }
    public bool? IsActive { get; set; }
    public string? RoleName { get; set; }
    public int MaxRowCount { get; set; } = 1000;
    public bool RequestedByHostAdmin { get; set; }
}

public sealed class UserImportValidatedRow
{
    public int RowNumber { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public bool IsActive { get; set; } = true;
    public List<string> RoleNames { get; set; } = [];
}

public sealed class UserImportPreparedPayload
{
    public string? FileName { get; set; }
    public List<UserImportValidatedRow> Rows { get; set; } = [];
}

public sealed class UserImportPreparedResult
{
    public UserImportPreparedPayload Payload { get; set; } = new();
    public Identity.Application.Contracts.Users.UserImportValidationDto Validation { get; set; } = new();
}
