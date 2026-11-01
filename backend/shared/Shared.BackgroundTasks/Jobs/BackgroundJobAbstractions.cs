namespace Shared.BackgroundTasks.Jobs;

public interface IBackgroundJob<TArgs>
{
    Task ExecuteAsync(BackgroundJobExecutionContext<TArgs> context);
}

public interface IBackgroundJobManager
{
    Task<string> EnqueueAsync<TArgs>(
        TArgs args,
        BackgroundJobEnqueueOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface IRecurringBackgroundJobManager
{
    Task AddOrUpdateAsync<TArgs>(
        string recurringJobId,
        TArgs args,
        string cronExpression,
        RecurringBackgroundJobOptions? options = null,
        CancellationToken cancellationToken = default);

    Task RemoveIfExistsAsync(string recurringJobId, CancellationToken cancellationToken = default);
}

public interface IBackgroundJobTenantArgs
{
    Guid? TenantId { get; }
}
