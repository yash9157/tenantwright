using Shared.BlobStoring;

namespace AuditLogs.Application.Auditing;

[BlobContainerName(Name)]
public sealed class AuditLogArchiveBlobContainer
{
    public const string Name = "auditlogs-archive";
}
