namespace Shared.DistributedLocking.Options;

public enum DistributedLockFailureMode
{
    FailClosed = 0,
    BestEffort = 1,
    BestEffortWithRetry = 2,
    PerUse = 3
}
