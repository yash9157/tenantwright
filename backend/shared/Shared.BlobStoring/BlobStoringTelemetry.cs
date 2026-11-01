using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Shared.BlobStoring;

internal static class BlobStoringTelemetry
{
    private static readonly Meter Meter = new("Shared.BlobStoring", "1.0.0");

    private static readonly Counter<long> OperationSuccessCounter = Meter.CreateCounter<long>(
        "shared_blobstoring_operation_success_total",
        unit: "count",
        description: "Number of successful blob operations.");

    private static readonly Counter<long> OperationFailureCounter = Meter.CreateCounter<long>(
        "shared_blobstoring_operation_failure_total",
        unit: "count",
        description: "Number of failed blob operations.");

    private static readonly Histogram<double> OperationDurationMs = Meter.CreateHistogram<double>(
        "shared_blobstoring_operation_duration_ms",
        unit: "ms",
        description: "Blob operation execution duration in milliseconds.");

    public static void RecordSuccess(string operation, string provider, double elapsedMs)
    {
        var tags = BuildTags(operation, provider, "success");
        OperationSuccessCounter.Add(1, tags);
        OperationDurationMs.Record(elapsedMs, tags);
    }

    public static void RecordFailure(string operation, string provider, string errorType, double elapsedMs)
    {
        var tags = BuildTags(operation, provider, "failure", errorType);
        OperationFailureCounter.Add(1, tags);
        OperationDurationMs.Record(elapsedMs, tags);
    }

    private static KeyValuePair<string, object?>[] BuildTags(string operation, string provider, string result, string? errorType = null)
    {
        if (string.IsNullOrWhiteSpace(errorType))
        {
            return
            [
                new("blob.operation", operation),
                new("blob.provider", provider),
                new("blob.result", result)
            ];
        }

        return
        [
            new("blob.operation", operation),
            new("blob.provider", provider),
            new("blob.result", result),
            new("blob.error_type", errorType)
        ];
    }
}
