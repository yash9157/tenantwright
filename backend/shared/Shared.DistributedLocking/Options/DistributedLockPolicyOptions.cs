namespace Shared.DistributedLocking.Options;

public sealed class DistributedLockPolicyOptions
{
    public int AcquireTimeoutMs { get; set; } = 1000;
    public int LeaseMs { get; set; } = 30000;
    public DistributedLockFailureMode FailureMode { get; set; } = DistributedLockFailureMode.FailClosed;
    public bool EnableMetrics { get; set; } = true;
    public int RetryCount { get; set; }
    public int RetryDelayMs { get; set; } = 75;
}
