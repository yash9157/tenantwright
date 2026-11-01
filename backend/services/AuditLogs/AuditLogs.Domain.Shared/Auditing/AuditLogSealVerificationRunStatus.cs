namespace AuditLogs.Domain.Shared.Auditing;

public enum AuditLogSealVerificationRunStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}
