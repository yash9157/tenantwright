using Shared.AuditLogs.Auditing;
using Shared.Domain;

namespace Identity.Domain.ApplicationUsers;

public class IdentityUserExportJob : IMultiTenant
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public string Status { get; set; } = IdentityUserJobStatuses.Queued;
    public string Format { get; set; } = "csv";
    public string FilterSnapshotJson { get; set; } = "{}";
    public int MaxRowCount { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? NextAttemptTimeUtc { get; set; }
    public int TryCount { get; set; }
    public DateTime ExpireAtUtc { get; set; }
    public string DownloadTokenHash { get; set; } = string.Empty;
    public DateTime DownloadTokenExpiresAtUtc { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public byte[]? FileContent { get; set; }
    public long? FileSizeBytes { get; set; }
    public long? RowCount { get; set; }
    public string? FailureReason { get; set; }
}
