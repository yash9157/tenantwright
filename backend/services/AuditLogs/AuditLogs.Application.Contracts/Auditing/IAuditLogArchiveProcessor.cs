namespace AuditLogs.Application.Contracts.Auditing;

public interface IAuditLogArchiveProcessor
{
    Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default);
}
