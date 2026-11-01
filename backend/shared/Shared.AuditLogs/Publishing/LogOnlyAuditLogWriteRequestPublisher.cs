using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.Auditing;

namespace Shared.AuditLogs.Publishing;

/// <summary>
/// The <see cref="IAuditLogWriteRequestPublisher"/> of a bus-free host (<c>Messaging:Provider=None</c>). There is no
/// broker to carry the audit write command, so each request is validated exactly as the durable path validates it and
/// then recorded through the <see cref="SimpleLogAuditingStore"/> log shape instead of failing the caller.
/// </summary>
/// <remarks>
/// Not conventionally registered: <see cref="SharedAuditLogsModule"/> selects it only when the provider is
/// <c>None</c>, so a host with a real provider can never silently downgrade its durable audit trail to logs.
/// </remarks>
public sealed class LogOnlyAuditLogWriteRequestPublisher(
    IOptions<SharedAuditingOptions> options,
    ILogger<SimpleLogAuditingStore> logger) : IAuditLogWriteRequestPublisher
{
    public Task PublishAsync(AuditLogWriteRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = AuditLogWriteRequestValidation.Normalize(
            request,
            AuditSourceService.Resolve(options.Value));
        SimpleLogAuditingStore.Record(logger, normalized.Category, normalized.SourceService, normalized.OccurredAtUtc);
        return Task.CompletedTask;
    }
}
