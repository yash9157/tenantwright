namespace Shared.DistributedLocking.Options;

public sealed class DistributedLockAcquireOptions
{
    public static DistributedLockAcquireOptions Default { get; } = new();

    public string? PolicyName { get; init; }
    public int? AcquireTimeoutMs { get; init; }
    public int? LeaseMs { get; init; }
    public DistributedLockFailureMode? FailureMode { get; init; }
    public bool? EnableMetrics { get; init; }
    public int? RetryCount { get; init; }
    public int? RetryDelayMs { get; init; }
    public bool IgnoreTenant { get; init; }
}
