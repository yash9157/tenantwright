namespace Shared.BackgroundTasks.Jobs;

public sealed class BackgroundJobExecutionContext<TArgs>
{
    public TArgs Args { get; }
    public BackgroundJobEnvelope Envelope { get; }
    public IServiceProvider ServiceProvider { get; }
    public CancellationToken CancellationToken { get; }

    public BackgroundJobExecutionContext(
        TArgs args,
        BackgroundJobEnvelope envelope,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        Args = args;
        Envelope = envelope;
        ServiceProvider = serviceProvider;
        CancellationToken = cancellationToken;
    }
}
