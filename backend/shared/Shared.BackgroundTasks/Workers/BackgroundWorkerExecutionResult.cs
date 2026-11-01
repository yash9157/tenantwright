namespace Shared.BackgroundTasks.Workers;

public readonly record struct BackgroundWorkerExecutionResult(
    bool RunAgainImmediately,
    Func<CancellationToken, Task>? WaitAsync)
{
    public static BackgroundWorkerExecutionResult Idle { get; } = new(false, null);

    public static BackgroundWorkerExecutionResult WorkCompleted { get; } = new(true, null);

    public static BackgroundWorkerExecutionResult Wait(Func<CancellationToken, Task> waitAsync)
    {
        ArgumentNullException.ThrowIfNull(waitAsync);
        return new BackgroundWorkerExecutionResult(false, waitAsync);
    }
}
