namespace AuditLogs.Application.Contracts.Auditing;

public interface IAuditLogExportProcessor
{
    Task ProcessJobAsync(long exportId, CancellationToken cancellationToken = default);
}
