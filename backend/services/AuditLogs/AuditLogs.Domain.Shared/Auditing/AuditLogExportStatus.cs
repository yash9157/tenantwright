namespace AuditLogs.Domain.Shared.Auditing;

public enum AuditLogExportStatus
{
    Queued = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    Expired = 4,
    Cancelled = 5
}
