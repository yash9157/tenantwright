namespace AuditLogs.Application.Contracts.Auditing;

public class PagedRequestDto
{
    public int SkipCount { get; set; }

    public int MaxResultCount { get; set; } = 50;
}

public class PagedResultDto<T>
{
    /// <summary>
    /// Total matching records, or <c>null</c> when the caller did not ask for a count.
    /// </summary>
    /// <remarks>
    /// Nullable so "not counted" is distinguishable from "none matched". Counting the audit table is the
    /// expensive half of a list request — a full scan of the filtered set on every cursor page — and a
    /// caller that only needs a page should not pay for it. A client that renders this must treat
    /// <c>null</c> as "unknown" rather than zero.
    /// </remarks>
    public long? TotalCount { get; set; }

    public List<T> Items { get; set; } = [];

    public string? NextCursor { get; set; }
}

public class GetAuditLogsInput : PagedRequestDto
{
    public string? Cursor { get; set; }

    /// <summary>
    /// Whether to count the whole filtered set alongside this page. Off by default: the count is a scan
    /// of every matching row, and cursor paging does not need it to navigate.
    /// </summary>
    public bool IncludeTotalCount { get; set; }

    public Guid? TenantId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public string? UserId { get; set; }

    public string? ApplicationName { get; set; }

    public string? Category { get; set; }

    public string? UrlContains { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? ClientIpAddress { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }
}

public class AuditLogSummaryDto
{
    public long Id { get; set; }

    public Guid EventId { get; set; }

    public Guid? TenantId { get; set; }

    public DateTime ExecutionTime { get; set; }

    public int ExecutionDuration { get; set; }

    public string ApplicationName { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string? UserId { get; set; }

    public string? UserName { get; set; }

    public string? HttpMethod { get; set; }

    public string? Url { get; set; }

    public int? HttpStatusCode { get; set; }
}

public class AuditLogDetailDto : AuditLogSummaryDto
{
    public bool SensitivePayloadRedacted { get; set; } = true;

    /// <summary>
    /// Result of re-verifying this row's HMAC signature against its persisted fields
    /// (sub-flow 1a: at-rest tamper-evidence). True = intact, false = signature mismatch (tampered),
    /// null = no signature was captured for this row (written before signing was enabled, or the
    /// signing key has since been rotated out of config).
    /// </summary>
    public bool? SignatureVerified { get; set; }

    public string? TenantName { get; set; }

    public string? ImpersonatorUserId { get; set; }

    public string? ImpersonatorUserName { get; set; }

    public Guid? ImpersonatorTenantId { get; set; }

    public string? ImpersonatorTenantName { get; set; }

    public string? ClientIpAddress { get; set; }

    public string? ClientName { get; set; }

    public string? ClientId { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string? BrowserInfo { get; set; }

    public List<AuditLogExceptionDto> Exceptions { get; set; } = [];

    public List<string> Comments { get; set; } = [];

    public Dictionary<string, string?> ExtraProperties { get; set; } = new(StringComparer.Ordinal);

    public List<AuditLogActionDto> Actions { get; set; } = [];

    public List<AuditLogEntityChangeDetailDto> EntityChanges { get; set; } = [];
}

public class AuditLogSensitivePayloadDto
{
    public long AuditLogId { get; set; }

    public List<AuditLogExceptionDto> Exceptions { get; set; } = [];

    public Dictionary<string, string?> ExtraProperties { get; set; } = new(StringComparer.Ordinal);

    public List<AuditLogSensitiveActionPayloadDto> Actions { get; set; } = [];
}

public class AuditLogSensitiveActionPayloadDto
{
    public long ActionId { get; set; }

    public string Parameters { get; set; } = "{}";

    public string? Result { get; set; }
}

public class AuditLogExceptionDto
{
    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? StackTrace { get; set; }
}

public class AuditLogActionDto
{
    public long Id { get; set; }

    public string ServiceName { get; set; } = string.Empty;

    public string MethodName { get; set; } = string.Empty;

    public string Parameters { get; set; } = "{}";

    public DateTime ExecutionTime { get; set; }

    public int ExecutionDuration { get; set; }

    public string? Result { get; set; }
}

public class AuditLogEntityChangeDetailDto
{
    public long Id { get; set; }

    public DateTime ChangeTime { get; set; }

    public string ChangeType { get; set; } = string.Empty;

    public Guid? EntityTenantId { get; set; }

    public string EntityTypeFullName { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public List<AuditLogEntityPropertyChangeDto> PropertyChanges { get; set; } = [];
}

public class AuditLogEntityPropertyChangeDto
{
    public long Id { get; set; }

    public string PropertyName { get; set; } = string.Empty;

    public string? PropertyTypeFullName { get; set; }

    public string? OriginalValue { get; set; }

    public string? NewValue { get; set; }
}

public class GetAuditLogEntityChangesInput : PagedRequestDto
{
    public string? Cursor { get; set; }

    /// <summary>
    /// Whether to count the whole filtered set alongside this page. Off by default: the count is a scan
    /// of every change recorded against the entity, and cursor paging does not need it to navigate.
    /// </summary>
    public bool IncludeTotalCount { get; set; }

    public Guid? TenantId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;
}

public class AuditLogEntityChangeDto
{
    public long Id { get; set; }

    public long AuditLogId { get; set; }

    public Guid? TenantId { get; set; }

    public DateTime ChangeTime { get; set; }

    public DateTime ExecutionTime { get; set; }

    public string EntityTypeFullName { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string ChangeType { get; set; } = string.Empty;
}

public class CreateAuditLogExportInput
{
    public Guid? TenantId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public string? UserId { get; set; }

    public string? ApplicationName { get; set; }

    public string? Category { get; set; }

    public string? UrlContains { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? ClientIpAddress { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string Format { get; set; } = "csv";

    public int? MaxRowCount { get; set; }
}

public class GetAuditLogExportsInput : PagedRequestDto
{
    /// <summary>Optional tenant filter (host only; ignored/validated against the current tenant otherwise).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Optional status filter (queued/processing/completed/failed/expired/cancelled).</summary>
    public string? Status { get; set; }

    public DateTime? RequestedFromUtc { get; set; }

    public DateTime? RequestedToUtc { get; set; }
}

public class AuditLogExportJobDto
{
    public long Id { get; set; }

    public Guid? TenantId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

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

    /// <summary>
    /// Deprecated compatibility field. Export creation and history responses always return <c>null</c>;
    /// callers must mint a short-lived token through the dedicated download-token endpoint.
    /// </summary>
    public string? DownloadToken { get; set; }
}

public class AuditLogExportDownloadDto
{
    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public byte[] Content { get; set; } = [];
}

public class AuditLogExportDownloadTokenDto
{
    public string DownloadToken { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }
}

public class GetAuditArchiveManifestsInput : PagedRequestDto
{
    public string? PartitionKeyContains { get; set; }

    public string? Status { get; set; }
}

public class AuditArchiveManifestDto
{
    public long Id { get; set; }

    public string PartitionKey { get; set; } = string.Empty;

    /// <summary>
    /// Deprecated compatibility field. Raw archive topology is never returned to browser callers.
    /// </summary>
    public string? ArchiveUri { get; set; }

    public string LocationLabel { get; set; } = string.Empty;

    public long RowCount { get; set; }

    public string Checksum { get; set; } = string.Empty;

    public DateTime ArchivedAtUtc { get; set; }

    public DateTime DeleteAfterUtc { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? LastFailureReason { get; set; }

    public DateTime? LastRestoreQueuedAtUtc { get; set; }

    public DateTime? LastRestoredAtUtc { get; set; }

    public long? LastRestoredRows { get; set; }
}

public class RequestAuditArchiveRestoreInput
{
    public string? Reason { get; set; }
}

public class VerifyAuditLogSealRangeInput
{
    public long FromId { get; set; }

    public long ToId { get; set; }
}

public class AuditLogSealVerificationDto
{
    public long Id { get; set; }

    public long FromId { get; set; }

    public long ToId { get; set; }

    public string Status { get; set; } = string.Empty;

    public bool? ChainIntact { get; set; }

    public int SealsChecked { get; set; }

    public long RowsChecked { get; set; }

    public List<string> Issues { get; set; } = [];

    public DateTime RequestedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}

public class GetAuditLogSealVerificationsInput : PagedRequestDto
{
    public string? Status { get; set; }
}

public class GetAuditLogSummaryInput
{
    public Guid? TenantId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    /// <summary>
    /// Max rows returned per ranked list (top users, failed actions, risky IPs). Clamped 1-100.
    /// </summary>
    public int TopCount { get; set; } = 10;
}

public class AuditLogSummaryReportDto
{
    public DateTime FromUtc { get; set; }

    public DateTime ToUtc { get; set; }

    /// <summary>
    /// UTC instant when the service finished computing this report.
    /// </summary>
    public DateTime GeneratedAtUtc { get; set; }

    public long TotalEvents { get; set; }

    public long FailedEvents { get; set; }

    public List<AuditLogTopUserDto> TopUsers { get; set; } = [];

    public List<AuditLogFailedActionDto> FailedActions { get; set; } = [];

    public List<AuditLogRiskyIpDto> RiskyIps { get; set; } = [];

    public List<AuditLogVolumePointDto> VolumeOverTime { get; set; } = [];
}

public class AuditLogTopUserDto
{
    public string? UserId { get; set; }

    public string? UserName { get; set; }

    public long EventCount { get; set; }
}

public class AuditLogFailedActionDto
{
    public string ApplicationName { get; set; } = string.Empty;

    public string? Url { get; set; }

    public int? HttpStatusCode { get; set; }

    public long FailureCount { get; set; }
}

public class AuditLogRiskyIpDto
{
    public string ClientIpAddress { get; set; } = string.Empty;

    public long FailureCount { get; set; }

    public long TotalCount { get; set; }

    public int DistinctUserCount { get; set; }
}

public class AuditLogVolumePointDto
{
    public DateTime BucketStartUtc { get; set; }

    public long EventCount { get; set; }

    public long FailedCount { get; set; }
}

public class CreateAuditLogLegalHoldInput
{
    public string Name { get; set; } = string.Empty;

    public Guid? TenantId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public string? UserId { get; set; }

    public string? Reason { get; set; }
}

public class ReleaseAuditLogLegalHoldInput
{
    public string? Reason { get; set; }
}

public class GetAuditLogLegalHoldsInput : PagedRequestDto
{
    public bool? IsReleased { get; set; }

    public Guid? TenantId { get; set; }
}

public class AuditLogLegalHoldDto
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid? TenantId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public string? UserId { get; set; }

    public string? Reason { get; set; }

    public bool IsReleased { get; set; }

    public DateTime? ReleasedAtUtc { get; set; }

    public DateTime CreationTime { get; set; }
}

public class AuditArchiveRestoreJobDto
{
    public long Id { get; set; }

    public long ManifestId { get; set; }

    public string PartitionKey { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTime RequestedAtUtc { get; set; }

    public string? Reason { get; set; }

    public string? RequestedByUserId { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public long? RestoredRows { get; set; }

    public string? FailureReason { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;
}

public class GetAuditArchiveRestoreJobsInput : PagedRequestDto
{
    public long? ManifestId { get; set; }

    public string? Status { get; set; }
}
