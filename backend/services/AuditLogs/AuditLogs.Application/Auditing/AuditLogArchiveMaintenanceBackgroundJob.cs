using AuditLogs.Application.Contracts.Auditing;
using Shared.BackgroundTasks.Jobs;

namespace AuditLogs.Application.Auditing;

/// <summary>
/// Recurring archive/restore maintenance. Shared-database-only by design: archive partitions, manifests, restore jobs
/// and blob names are keyed per database without a tenant-database discriminator, and archive metadata entities are
/// not tenant-owned (repositories route them to the shared database), so running this in a dedicated tenant scope
/// would mix that tenant's rows into the shared archive. Dedicated tenant AuditLogs databases are sealed (see
/// <c>AuditLogSealWorker</c>) but not archived; their rows stay hot until a per-database archive model exists.
/// </summary>
internal sealed class AuditLogArchiveMaintenanceBackgroundJob : IBackgroundJob<AuditLogArchiveMaintenanceJobArgs>
{
    private readonly IAuditLogArchiveProcessor _processor;

    public AuditLogArchiveMaintenanceBackgroundJob(IAuditLogArchiveProcessor processor)
    {
        _processor = processor;
    }

    public async Task ExecuteAsync(BackgroundJobExecutionContext<AuditLogArchiveMaintenanceJobArgs> context)
    {
        await _processor.ProcessBatchAsync(context.CancellationToken);
    }
}
