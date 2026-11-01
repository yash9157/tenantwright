using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Shared.MultiTenancy;

public static class TenantResolutionMetrics
{
    private static readonly Meter Meter = new("Shared.MultiTenancy");
    private static readonly Counter<long> ResolutionCounter =
        Meter.CreateCounter<long>("shared.tenant.resolve.count");
    private static readonly Histogram<double> ResolutionDurationMs =
        Meter.CreateHistogram<double>("shared.tenant.resolve.duration.ms");
    private static readonly Counter<long> StoreCacheCounter =
        Meter.CreateCounter<long>("shared.tenant.store.cache.operations");

    public static Stopwatch StartTimer()
    {
        return Stopwatch.StartNew();
    }

    public static void RecordResolution(TenantResolveResult result)
    {
        ResolutionCounter.Add(
            1,
            new KeyValuePair<string, object?>("status", result.Status.ToString()),
            new KeyValuePair<string, object?>("source", result.Source ?? "none"));
    }

    public static void RecordResolutionDuration(Stopwatch timer, TenantResolveResult result)
    {
        timer.Stop();

        ResolutionDurationMs.Record(
            timer.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("status", result.Status.ToString()),
            new KeyValuePair<string, object?>("source", result.Source ?? "none"));
    }

    public static void RecordStoreCacheOperation(string cacheName, string result)
    {
        StoreCacheCounter.Add(
            1,
            new KeyValuePair<string, object?>("cache", cacheName),
            new KeyValuePair<string, object?>("result", result));
    }
}
