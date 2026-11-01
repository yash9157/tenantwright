using Shared.BackgroundTasks.Jobs;
using Shared.Uow.Attributes;

namespace Identity.Application.Users;

internal sealed class IdentityUserImportBackgroundJob : IBackgroundJob<IdentityUserImportJobArgs>
{
    private readonly IdentityUserImportProcessor _processor;

    public IdentityUserImportBackgroundJob(IdentityUserImportProcessor processor)
    {
        _processor = processor;
    }

    // Non-transactional whole-job scope: the processor owns an explicit per-chunk relational
    // transaction for resumable progress, so the job must not hold one transaction across the
    // entire payload.
    [UnitOfWork(isTransactional: false)]
    public Task ExecuteAsync(BackgroundJobExecutionContext<IdentityUserImportJobArgs> context)
    {
        return _processor.ProcessJobAsync(context.Args.JobId, context.CancellationToken);
    }
}
