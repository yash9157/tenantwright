using Hangfire;
using Shared.BackgroundTasks.Jobs;

namespace Shared.BackgroundTasks.Hangfire;

public sealed class HangfireRecurringBackgroundJobManager : IRecurringBackgroundJobManager
{
    private readonly global::Hangfire.IRecurringJobManager _recurringJobManager;
    private readonly IBackgroundJobEnvelopeFactory _envelopeFactory;

    public HangfireRecurringBackgroundJobManager(
        global::Hangfire.IRecurringJobManager recurringJobManager,
        IBackgroundJobEnvelopeFactory envelopeFactory)
    {
        _recurringJobManager = recurringJobManager;
        _envelopeFactory = envelopeFactory;
    }

    public Task AddOrUpdateAsync<TArgs>(
        string recurringJobId,
        TArgs args,
        string cronExpression,
        RecurringBackgroundJobOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(recurringJobId))
        {
            throw new ArgumentException("Recurring job id is required.", nameof(recurringJobId));
        }

        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            throw new ArgumentException("Cron expression is required.", nameof(cronExpression));
        }

        var envelope = _envelopeFactory.CreateRecurring(args, options);
        var queue = HangfireQueueSelector.SelectQueue(envelope, recurring: true);
        var job = global::Hangfire.Common.Job.FromExpression<HangfireBackgroundJobDispatcher>(
            dispatcher => dispatcher.DispatchAsync(envelope, CancellationToken.None));

#pragma warning disable CS0618 // Hangfire 1.8 interface lacks the queue overload; keep queue explicit through options until 2.0.
        _recurringJobManager.AddOrUpdate(
            recurringJobId.Trim(),
            job,
            cronExpression.Trim(),
            new RecurringJobOptions
            {
                QueueName = queue,
                TimeZone = options?.TimeZone ?? TimeZoneInfo.Utc
            });
#pragma warning restore CS0618

        return Task.CompletedTask;
    }

    public Task RemoveIfExistsAsync(string recurringJobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(recurringJobId))
        {
            _recurringJobManager.RemoveIfExists(recurringJobId.Trim());
        }

        return Task.CompletedTask;
    }
}
