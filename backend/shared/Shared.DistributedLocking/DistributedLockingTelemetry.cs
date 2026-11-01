using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Shared.DistributedLocking.Options;

namespace Shared.DistributedLocking;

internal static class DistributedLockingTelemetry
{
    private static readonly Meter Meter = new("Shared.DistributedLocking", "1.0.0");

    private static readonly Counter<long> AcquireSuccessCounter = Meter.CreateCounter<long>(
        "shared_distributed_lock_acquire_success_total",
        unit: "count",
        description: "Number of successful distributed lock acquisitions.");

    private static readonly Counter<long> AcquireContentionCounter = Meter.CreateCounter<long>(
        "shared_distributed_lock_acquire_contention_total",
        unit: "count",
        description: "Number of distributed lock acquisition attempts that did not acquire due to contention.");

    private static readonly Counter<long> AcquireFailureCounter = Meter.CreateCounter<long>(
        "shared_distributed_lock_acquire_failure_total",
        unit: "count",
        description: "Number of distributed lock acquisition failures caused by backend errors.");

    private static readonly Histogram<double> AcquireDurationMs = Meter.CreateHistogram<double>(
        "shared_distributed_lock_acquire_duration_ms",
        unit: "ms",
        description: "Time spent attempting distributed lock acquisition.");

    public static void RecordSuccess(string policyName, DistributedLockFailureMode failureMode, double elapsedMs)
    {
        AcquireSuccessCounter.Add(1, BuildTags(policyName, failureMode));
        AcquireDurationMs.Record(elapsedMs, BuildTags(policyName, failureMode));
    }

    public static void RecordContention(string policyName, DistributedLockFailureMode failureMode, double elapsedMs)
    {
        AcquireContentionCounter.Add(1, BuildTags(policyName, failureMode));
        AcquireDurationMs.Record(elapsedMs, BuildTags(policyName, failureMode));
    }

    public static void RecordFailure(string policyName, DistributedLockFailureMode failureMode, string errorType, double elapsedMs)
    {
        var tags = BuildTags(policyName, failureMode);
        Array.Resize(ref tags, tags.Length + 1);
        tags[^1] = new KeyValuePair<string, object?>("lock.error_type", errorType);

        AcquireFailureCounter.Add(1, tags);
        AcquireDurationMs.Record(elapsedMs, tags);
    }

    private static KeyValuePair<string, object?>[] BuildTags(string policyName, DistributedLockFailureMode failureMode)
    {
        return
        [
            new KeyValuePair<string, object?>("lock.policy", policyName),
            new KeyValuePair<string, object?>("lock.failure_mode", failureMode.ToString())
        ];
    }
}
