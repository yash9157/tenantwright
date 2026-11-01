using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Notifications.Domain.Notifications;

/// <summary>
/// An immutable-input, replaceable aggregate for one completed delivery-latency hour.
/// Latency is strictly the completed provider-send interval, never time spent queued.
/// </summary>
public sealed class NotificationDeliveryLatencyRollup : Entity<long>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string ScopeKey { get; private set; } = default!;
    public NotificationChannel Channel { get; private set; }
    public DateTime BucketUtc { get; private set; }
    public int SampleCount { get; private set; }
    public long P50Ms { get; private set; }
    public long P95Ms { get; private set; }
    public long P99Ms { get; private set; }
    public DateTime CalculatedAtUtc { get; private set; }

    private NotificationDeliveryLatencyRollup()
    {
    }

    public NotificationDeliveryLatencyRollup(
        long id,
        Guid? tenantId,
        NotificationChannel channel,
        DateTime bucketUtc,
        int sampleCount,
        long p50Ms,
        long p95Ms,
        long p99Ms,
        DateTime calculatedAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        ScopeKey = tenantId?.ToString("D") ?? "host";
        Channel = channel;
        BucketUtc = NormalizeHour(bucketUtc);
        Replace(sampleCount, p50Ms, p95Ms, p99Ms, calculatedAtUtc);
    }

    public void Replace(int sampleCount, long p50Ms, long p95Ms, long p99Ms, DateTime calculatedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);

        if (p50Ms < 0 || p95Ms < p50Ms || p99Ms < p95Ms)
        {
            throw new ArgumentOutOfRangeException(nameof(p99Ms), "Latency percentiles must be non-negative and monotonic.");
        }

        SampleCount = sampleCount;
        P50Ms = p50Ms;
        P95Ms = p95Ms;
        P99Ms = p99Ms;
        CalculatedAtUtc = calculatedAtUtc.Kind == DateTimeKind.Utc
            ? calculatedAtUtc
            : calculatedAtUtc.ToUniversalTime();
    }

    private static DateTime NormalizeHour(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
    }
}
