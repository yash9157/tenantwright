namespace Shared.BackgroundTasks.Workers;

public sealed class BackgroundWorkerContext
{
    public IServiceProvider ServiceProvider { get; }
    public string WorkerName { get; }
    public int ConsecutiveFailureCount { get; }
    public DateTime IterationStartedAtUtc { get; }
    public CancellationToken CancellationToken { get; }

    public BackgroundWorkerContext(
        IServiceProvider serviceProvider,
        string workerName,
        int consecutiveFailureCount,
        DateTime iterationStartedAtUtc,
        CancellationToken cancellationToken)
    {
        ServiceProvider = serviceProvider;
        WorkerName = workerName;
        ConsecutiveFailureCount = consecutiveFailureCount;
        IterationStartedAtUtc = iterationStartedAtUtc;
        CancellationToken = cancellationToken;
    }
}
