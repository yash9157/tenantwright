namespace Shared.BackgroundTasks.Jobs;

internal sealed class DisabledBackgroundJobManager : IBackgroundJobManager
{
    public Task<string> EnqueueAsync<TArgs>(
        TArgs args,
        BackgroundJobEnqueueOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "No background job provider is registered. Add a provider module such as Shared.BackgroundTasks.Hangfire.");
    }
}

internal sealed class DisabledRecurringBackgroundJobManager : IRecurringBackgroundJobManager
{
    public Task AddOrUpdateAsync<TArgs>(
        string recurringJobId,
        TArgs args,
        string cronExpression,
        RecurringBackgroundJobOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "No recurring background job provider is registered. Add a provider module such as Shared.BackgroundTasks.Hangfire.");
    }

    public Task RemoveIfExistsAsync(string recurringJobId, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "No recurring background job provider is registered. Add a provider module such as Shared.BackgroundTasks.Hangfire.");
    }
}
