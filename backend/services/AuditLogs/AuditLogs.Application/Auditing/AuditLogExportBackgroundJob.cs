using AuditLogs.Application.Contracts.Auditing;
using Shared.BackgroundTasks.Jobs;

namespace AuditLogs.Application.Auditing;

internal sealed class AuditLogExportBackgroundJob : IBackgroundJob<AuditLogExportJobArgs>
{
    private readonly IAuditLogExportProcessor _processor;

    public AuditLogExportBackgroundJob(IAuditLogExportProcessor processor)
    {
        _processor = processor;
    }

    public Task ExecuteAsync(BackgroundJobExecutionContext<AuditLogExportJobArgs> context)
    {
        return _processor.ProcessJobAsync(context.Args.ExportId, context.CancellationToken);
    }
}
