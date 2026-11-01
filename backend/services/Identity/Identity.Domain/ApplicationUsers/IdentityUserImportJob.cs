using Shared.AuditLogs.Auditing;
using Shared.Domain;

namespace Identity.Domain.ApplicationUsers;

public class IdentityUserImportJob : IMultiTenant
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public string Status { get; set; } = IdentityUserJobStatuses.Queued;
    public string? FileName { get; set; }
    public string ProtectedPayload { get; set; } = string.Empty;
    public string ErrorSnapshotJson { get; set; } = "[]";
    public int TotalRowCount { get; set; }
    public int ValidRowCount { get; set; }
    public int CreatedCount { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? NextAttemptTimeUtc { get; set; }
    public int TryCount { get; set; }
    public string? FailureReason { get; set; }
}
