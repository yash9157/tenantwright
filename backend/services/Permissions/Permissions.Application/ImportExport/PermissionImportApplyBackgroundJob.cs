using Shared.BackgroundTasks.Jobs;

namespace Permissions.Application.ImportExport;

internal sealed class PermissionImportApplyBackgroundJob : IBackgroundJob<PermissionImportApplyJobArgs>
{
    private readonly PermissionImportApplyProcessor _processor;

    public PermissionImportApplyBackgroundJob(PermissionImportApplyProcessor processor)
    {
        _processor = processor;
    }

    public Task ExecuteAsync(BackgroundJobExecutionContext<PermissionImportApplyJobArgs> context)
    {
        return _processor.ProcessAsync(context.Args.JobId, context.CancellationToken);
    }
}
