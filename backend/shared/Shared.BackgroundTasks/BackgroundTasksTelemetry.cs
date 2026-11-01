using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Shared.BackgroundTasks;

internal static class BackgroundTasksTelemetry
{
    public const string MeterName = "Shared.BackgroundTasks";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> JobExecutionCounter =
        Meter.CreateCounter<long>("shared.background_tasks.job.executions_total");
    private static readonly Counter<long> JobFailureCounter =
        Meter.CreateCounter<long>("shared.background_tasks.job.failures_total");
    private static readonly Histogram<double> JobDurationMs =
        Meter.CreateHistogram<double>("shared.background_tasks.job.duration.ms");
    private static readonly Counter<long> WorkerIterationCounter =
        Meter.CreateCounter<long>("shared.background_tasks.worker.iterations_total");
    private static readonly Counter<long> WorkerFailureCounter =
        Meter.CreateCounter<long>("shared.background_tasks.worker.failures_total");
    private static readonly Counter<long> WorkerLockSkippedCounter =
        Meter.CreateCounter<long>("shared.background_tasks.worker.lock_skipped_total");
    private static readonly Histogram<double> WorkerIterationDurationMs =
        Meter.CreateHistogram<double>("shared.background_tasks.worker.iteration.duration.ms");
    private static readonly Histogram<double> WorkerDelayMs =
        Meter.CreateHistogram<double>("shared.background_tasks.worker.delay.ms");
    private static readonly Counter<long> WorkerWakeCounter =
        Meter.CreateCounter<long>("shared.background_tasks.worker.wake_total");

    public static long StartTimestamp() => Stopwatch.GetTimestamp();

    public static void RecordJobExecution(string jobName, long startedAt, bool succeeded)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("job.name", jobName),
            new("status", succeeded ? "success" : "failure")
        };

        JobExecutionCounter.Add(1, tags);
        if (!succeeded)
        {
            JobFailureCounter.Add(1, new KeyValuePair<string, object?>("job.name", jobName));
        }

        JobDurationMs.Record(ElapsedMilliseconds(startedAt), tags);
    }

    public static void RecordWorkerIteration(string workerName, long startedAt, bool succeeded)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("worker.name", workerName),
            new("status", succeeded ? "success" : "failure")
        };

        WorkerIterationCounter.Add(1, tags);
        if (!succeeded)
        {
            WorkerFailureCounter.Add(1, new KeyValuePair<string, object?>("worker.name", workerName));
        }

        WorkerIterationDurationMs.Record(ElapsedMilliseconds(startedAt), tags);
    }

    public static void RecordWorkerLockSkipped(string workerName, string lockName)
    {
        WorkerLockSkippedCounter.Add(
            1,
            new KeyValuePair<string, object?>("worker.name", workerName),
            new KeyValuePair<string, object?>("lock.name", lockName));
    }

    public static void RecordWorkerDelay(string workerName, int delayMs, bool afterFailure)
    {
        WorkerDelayMs.Record(
            delayMs,
            new KeyValuePair<string, object?>("worker.name", workerName),
            new KeyValuePair<string, object?>("after_failure", afterFailure));
    }

    public static void RecordWorkerWake(string? key, string reason)
    {
        WorkerWakeCounter.Add(
            1,
            new KeyValuePair<string, object?>("wake.key", key),
            new KeyValuePair<string, object?>("reason", reason));
    }

    private static double ElapsedMilliseconds(long startedAt)
    {
        return Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
    }
}
