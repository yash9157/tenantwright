namespace Shared.AuditLogs.Publishing;

public interface IAuditLogWriteRequestPublisher
{
    Task PublishAsync(
        AuditLogWriteRequest request,
        CancellationToken cancellationToken = default);
}
