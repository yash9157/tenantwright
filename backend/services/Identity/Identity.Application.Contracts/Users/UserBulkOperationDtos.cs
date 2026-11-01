namespace Identity.Application.Contracts.Users;

public class BulkUserActionInput
{
    public List<string> UserIds { get; set; } = [];
}

public class BulkUserActionResultDto
{
    public int RequestedCount { get; set; }
    public int ProcessedCount { get; set; }
}

public class RequestUserExportInput
{
    public string? Filter { get; set; }
    public bool? IsActive { get; set; }
    public string? RoleName { get; set; }
    public string Format { get; set; } = "csv";
    public int? MaxRowCount { get; set; }
}

public class UserExportJobDto
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Format { get; set; } = "csv";
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime ExpireAtUtc { get; set; }
    public long? RowCount { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? FailureReason { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public bool IsReadyForDownload { get; set; }
    public string? DownloadToken { get; set; }
}

public class UserExportDownloadDto
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Content { get; set; } = [];
}

public class ValidateUserImportInput
{
    public string? FileName { get; set; }
    public string CsvContent { get; set; } = string.Empty;
}

public class StartUserImportInput : ValidateUserImportInput
{
}

public class UserImportValidationDto
{
    public int TotalRowCount { get; set; }
    public int ValidRowCount { get; set; }
    public List<UserImportValidationErrorDto> Errors { get; set; } = [];
}

public class UserImportValidationErrorDto
{
    public int RowNumber { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class UserImportJobDto
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int TotalRowCount { get; set; }
    public int CreatedCount { get; set; }
    public string? FailureReason { get; set; }
    public List<UserImportValidationErrorDto> Errors { get; set; } = [];
}
