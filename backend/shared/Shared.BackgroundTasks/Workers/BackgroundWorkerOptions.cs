namespace Shared.BackgroundTasks.Workers;

public class BackgroundWorkerOptions
{
    public bool Enabled { get; set; } = true;
    public string? WorkerName { get; set; }
    public int PollIntervalMs { get; set; } = 1000;
    public int ErrorDelayMs { get; set; } = 5000;
    public int JitterMs { get; set; }
    public int MaxConsecutiveFailures { get; set; } = 10;
    public string? DistributedLockName { get; set; }
    public int OwnershipAcquireTimeoutMs { get; set; } = 100;
    public int OwnershipLeaseMs { get; set; } = 5000;
}

public static class BackgroundWorkerOptionsPostConfigure
{
    public static void Normalize(BackgroundWorkerOptions options)
    {
        options.WorkerName = string.IsNullOrWhiteSpace(options.WorkerName)
            ? "shared-periodic-worker"
            : options.WorkerName.Trim();
        options.PollIntervalMs = Math.Clamp(options.PollIntervalMs, 250, 86_400_000);
        options.ErrorDelayMs = Math.Clamp(options.ErrorDelayMs, 250, 300_000);
        options.JitterMs = Math.Clamp(options.JitterMs, 0, 60_000);
        options.MaxConsecutiveFailures = Math.Clamp(options.MaxConsecutiveFailures, 1, 1000);
        options.DistributedLockName = string.IsNullOrWhiteSpace(options.DistributedLockName)
            ? null
            : options.DistributedLockName.Trim();
        options.OwnershipAcquireTimeoutMs = Math.Clamp(options.OwnershipAcquireTimeoutMs, 1, 30_000);
        options.OwnershipLeaseMs = Math.Clamp(options.OwnershipLeaseMs, 500, 300_000);
    }
}
