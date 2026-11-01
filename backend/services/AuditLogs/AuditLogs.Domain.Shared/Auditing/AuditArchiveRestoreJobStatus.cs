namespace AuditLogs.Domain.Shared.Auditing;

public enum AuditArchiveRestoreJobStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}
