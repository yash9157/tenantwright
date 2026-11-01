using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Shared.Caching;

internal static class CachingTelemetry
{
    private static readonly Meter Meter = new("Shared.Caching", "1.0.0");

    private static readonly Counter<long> CacheHitCounter = Meter.CreateCounter<long>(
        "shared_cache_hits_total",
        unit: "count",
        description: "Number of successful cache hit operations.");

    private static readonly Counter<long> CacheMissCounter = Meter.CreateCounter<long>(
        "shared_cache_misses_total",
        unit: "count",
        description: "Number of cache miss operations.");

    private static readonly Counter<long> CacheFactoryCounter = Meter.CreateCounter<long>(
        "shared_cache_factory_calls_total",
        unit: "count",
        description: "Number of cache factory invocations.");

    private static readonly Counter<long> CacheExceptionCounter = Meter.CreateCounter<long>(
        "shared_cache_exceptions_total",
        unit: "count",
        description: "Number of cache exceptions.");

    private static readonly Histogram<long> CacheBatchSizeHistogram = Meter.CreateHistogram<long>(
        "shared_cache_batch_size",
        unit: "count",
        description: "Batch size for multi-key cache operations.");

    public static void RecordHit(string cacheName, string operation)
    {
        CacheHitCounter.Add(1, BuildTags(cacheName, operation));
    }

    public static void RecordMiss(string cacheName, string operation)
    {
        CacheMissCounter.Add(1, BuildTags(cacheName, operation));
    }

    public static void RecordFactory(string cacheName, string operation)
    {
        CacheFactoryCounter.Add(1, BuildTags(cacheName, operation));
    }

    public static void RecordException(string cacheName, string operation)
    {
        CacheExceptionCounter.Add(1, BuildTags(cacheName, operation));
    }

    public static void RecordBatchSize(string cacheName, string operation, int size)
    {
        CacheBatchSizeHistogram.Record(size, BuildTags(cacheName, operation));
    }

    private static KeyValuePair<string, object?>[] BuildTags(string cacheName, string operation)
    {
        return
        [
            new KeyValuePair<string, object?>("cache.name", cacheName),
            new KeyValuePair<string, object?>("cache.operation", operation)
        ];
    }
}
