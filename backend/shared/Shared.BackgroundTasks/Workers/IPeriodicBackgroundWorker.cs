namespace Shared.BackgroundTasks.Workers;

public interface IPeriodicBackgroundWorker
{
    Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context);
}
