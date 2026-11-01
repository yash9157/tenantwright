namespace AuditLogs.Domain.Shared.Auditing;

public enum AuditArchiveManifestStatus : byte
{
    PendingArchive = 0,
    Archiving = 1,
    Archived = 2,
    ArchiveFailed = 3,
    RestoreQueued = 4,
    Restoring = 5,
    Restored = 6,
    RestoreFailed = 7,
    Deleted = 8
}
