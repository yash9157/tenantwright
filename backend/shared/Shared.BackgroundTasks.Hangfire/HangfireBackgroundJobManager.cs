using Hangfire;
using Hangfire.States;
using Shared.BackgroundTasks.Jobs;

namespace Shared.BackgroundTasks.Hangfire;

public sealed class HangfireBackgroundJobManager : IBackgroundJobManager
{
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IBackgroundJobEnvelopeFactory _envelopeFactory;

    public HangfireBackgroundJobManager(
        IBackgroundJobClient backgroundJobClient,
        IBackgroundJobEnvelopeFactory envelopeFactory)
    {
        _backgroundJobClient = backgroundJobClient;
        _envelopeFactory = envelopeFactory;
    }

    public Task<string> EnqueueAsync<TArgs>(
        TArgs args,
        BackgroundJobEnqueueOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var envelope = _envelopeFactory.Create(args, options);
        IState state = options?.Delay is { } delay && delay > TimeSpan.Zero
            ? new ScheduledState(DateTime.UtcNow.Add(delay))
            : new EnqueuedState(HangfireQueueSelector.SelectQueue(envelope));

        var jobId = _backgroundJobClient.Create<HangfireBackgroundJobDispatcher>(
            dispatcher => dispatcher.DispatchAsync(envelope, CancellationToken.None),
            state);

        return Task.FromResult(jobId);
    }
}
