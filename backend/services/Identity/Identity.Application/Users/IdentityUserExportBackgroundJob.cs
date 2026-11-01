using Shared.BackgroundTasks.Jobs;
using Shared.Uow.Attributes;

namespace Identity.Application.Users;

internal sealed class IdentityUserExportBackgroundJob : IBackgroundJob<IdentityUserExportJobArgs>
{
    private readonly IdentityUserExportProcessor _processor;

    public IdentityUserExportBackgroundJob(IdentityUserExportProcessor processor)
    {
        _processor = processor;
    }

    // Non-transactional whole-job scope: export queries and CSV generation may be long-running and
    // must not hold a database transaction.
    [UnitOfWork(isTransactional: false)]
    public Task ExecuteAsync(BackgroundJobExecutionContext<IdentityUserExportJobArgs> context)
    {
        return _processor.ProcessJobAsync(context.Args.JobId, context.CancellationToken);
    }
}
