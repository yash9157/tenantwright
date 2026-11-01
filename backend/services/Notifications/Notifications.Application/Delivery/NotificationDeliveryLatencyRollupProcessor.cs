using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Providers;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Abstractions;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
namespace Notifications.Application.Delivery;

public sealed class NotificationsDeliveryLatencyRollupOptions
{
    public bool Enabled { get; set; } = true;
    public int LookbackHours { get; set; } = 48;
    public int MaxBucketsPerRun { get; set; } = 24;
    public int IntervalSeconds { get; set; } = 300;
}

public sealed class NotificationDeliveryLatencyRollupProcessor : INotificationDeliveryLatencyRollupProcessor, ITransientDependency
{
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly IDbContextProvider<NotificationsDbContextApp> _dataContextProvider;
    private readonly NotificationsDeliveryLatencyRollupOptions _options;

    public NotificationDeliveryLatencyRollupProcessor(
        IUnitOfWorkExecutor unitOfWorkExecutor,
        IDbContextProvider<NotificationsDbContextApp> dataContextProvider,
        IOptions<NotificationsDeliveryLatencyRollupOptions> options)
    {
        _unitOfWorkExecutor = unitOfWorkExecutor;
        _dataContextProvider = dataContextProvider;
        _options = options.Value;
    }

    public async Task<NotificationDeliveryLatencyRollupBatchResult> RollupBatchAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return new NotificationDeliveryLatencyRollupBatchResult(0, false);
        }

        var calculatedAtUtc = DateTime.UtcNow;
        var completedHour = new DateTime(
            calculatedAtUtc.Year,
            calculatedAtUtc.Month,
            calculatedAtUtc.Day,
            calculatedAtUtc.Hour,
            0,
            0,
            DateTimeKind.Utc);
        var fromUtc = completedHour.AddHours(-Math.Clamp(_options.LookbackHours, 1, 168));
        var maxBuckets = Math.Clamp(_options.MaxBucketsPerRun, 1, 72);

        var candidates = await _unitOfWorkExecutor.ExecuteNonTransactionalAsync(
            async operationCancellationToken =>
            {
                var queryDbContext = await _dataContextProvider.GetDbContextAsync();
                return await (
                    from delivery in queryDbContext.NotificationDeliveries.IgnoreQueryFilters().AsNoTracking()
                    join notification in queryDbContext.Notifications.IgnoreQueryFilters().WhereOwnedByCurrentDatabase().AsNoTracking()
                        on delivery.NotificationId equals notification.Id
                    where (delivery.Status == NotificationStatus.ProviderAccepted ||
                           delivery.Status == NotificationStatus.Delivered) &&
                          delivery.DeliveryAttemptedAtUtc.HasValue &&
                          delivery.SentTimeUtc.HasValue &&
                          delivery.SentTimeUtc.GetValueOrDefault() >= fromUtc &&
                          delivery.SentTimeUtc.GetValueOrDefault() < completedHour &&
                          delivery.SentTimeUtc.GetValueOrDefault() >= delivery.DeliveryAttemptedAtUtc.GetValueOrDefault()
                    let sentTimeUtc = delivery.SentTimeUtc.GetValueOrDefault()
                    let bucketUtc = new DateTime(
                        sentTimeUtc.Year,
                        sentTimeUtc.Month,
                        sentTimeUtc.Day,
                        sentTimeUtc.Hour,
                        0,
                        0,
                        DateTimeKind.Utc)
                    join rollup in queryDbContext.NotificationDeliveryLatencyRollups.IgnoreQueryFilters().AsNoTracking()
                        on new
                        {
                            ScopeKey = notification.TenantId.HasValue
                                ? notification.TenantId.Value.ToString()
                                : "host",
                            notification.Channel,
                            BucketUtc = bucketUtc
                        }
                        equals new
                        {
                            rollup.ScopeKey,
                            rollup.Channel,
                            rollup.BucketUtc
                        }
                        into matchingRollups
                    from rollup in matchingRollups.DefaultIfEmpty()
                    group 1 by new
                    {
                        notification.TenantId,
                        notification.Channel,
                        BucketUtc = bucketUtc,
                        PreviousCalculationUtc = rollup == null ? (DateTime?)null : rollup.CalculatedAtUtc
                    }
                    into bucket
                    orderby bucket.Key.PreviousCalculationUtc == null descending,
                        bucket.Key.PreviousCalculationUtc,
                        bucket.Key.BucketUtc,
                        bucket.Key.TenantId,
                        bucket.Key.Channel
                    select new LatencyBucketCandidate(
                        bucket.Key.TenantId,
                        bucket.Key.Channel,
                        bucket.Key.BucketUtc,
                        bucket.Key.PreviousCalculationUtc))
                    .Take(maxBuckets + 1)
                    .ToListAsync(operationCancellationToken);
            },
            cancellationToken);

        var selectedCandidates = candidates.Take(maxBuckets).ToArray();
        var hasMoreUnrolledBuckets = candidates
            .Skip(maxBuckets)
            .Any(candidate => candidate.PreviousCalculationUtc == null);
        var processed = 0;

        foreach (var bucket in selectedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bucketProcessed = await _unitOfWorkExecutor.ExecuteTransactionalAsync(
                async operationCancellationToken =>
                {
                    var writeDbContext = await _dataContextProvider.GetDbContextAsync();
                    var bucketEndUtc = bucket.BucketUtc.AddHours(1);
                    var samples = await (
                        from delivery in writeDbContext.NotificationDeliveries.IgnoreQueryFilters().AsNoTracking()
                        join notification in writeDbContext.Notifications.IgnoreQueryFilters().AsNoTracking()
                            on delivery.NotificationId equals notification.Id
                        where notification.TenantId == bucket.TenantId &&
                              notification.Channel == bucket.Channel &&
                              (delivery.Status == NotificationStatus.ProviderAccepted ||
                               delivery.Status == NotificationStatus.Delivered) &&
                              delivery.DeliveryAttemptedAtUtc.HasValue &&
                              delivery.SentTimeUtc.HasValue &&
                              delivery.SentTimeUtc.GetValueOrDefault() >= bucket.BucketUtc &&
                              delivery.SentTimeUtc.GetValueOrDefault() < bucketEndUtc &&
                              delivery.SentTimeUtc.GetValueOrDefault() >= delivery.DeliveryAttemptedAtUtc.GetValueOrDefault()
                        select (long)(delivery.SentTimeUtc.GetValueOrDefault() - delivery.DeliveryAttemptedAtUtc.GetValueOrDefault()).TotalMilliseconds)
                        .OrderBy(value => value)
                        .ToListAsync(operationCancellationToken);

                    if (samples.Count == 0)
                    {
                        return false;
                    }

                    var scopeKey = bucket.TenantId?.ToString("D") ?? "host";
                    var rollup = await writeDbContext.NotificationDeliveryLatencyRollups
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(
                            item => item.ScopeKey == scopeKey && item.Channel == bucket.Channel && item.BucketUtc == bucket.BucketUtc,
                            operationCancellationToken);
                    if (rollup == null)
                    {
                        rollup = new NotificationDeliveryLatencyRollup(
                            id: 0,
                            tenantId: bucket.TenantId,
                            channel: bucket.Channel,
                            bucketUtc: bucket.BucketUtc,
                            sampleCount: samples.Count,
                            p50Ms: Percentile(samples, 0.50),
                            p95Ms: Percentile(samples, 0.95),
                            p99Ms: Percentile(samples, 0.99),
                            calculatedAtUtc: calculatedAtUtc);
                        await writeDbContext.NotificationDeliveryLatencyRollups.AddAsync(
                            rollup,
                            operationCancellationToken);
                    }
                    else
                    {
                        rollup.Replace(
                            samples.Count,
                            Percentile(samples, 0.50),
                            Percentile(samples, 0.95),
                            Percentile(samples, 0.99),
                            calculatedAtUtc);
                    }

                    await writeDbContext.SaveChangesAsync(operationCancellationToken);
                    return true;
                },
                cancellationToken);

            if (bucketProcessed)
            {
                processed++;
            }
        }

        return new NotificationDeliveryLatencyRollupBatchResult(processed, hasMoreUnrolledBuckets);
    }

    private static long Percentile(List<long> orderedSamples, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * orderedSamples.Count) - 1;
        return orderedSamples[Math.Clamp(index, 0, orderedSamples.Count - 1)];
    }

    private sealed record LatencyBucketCandidate(
        Guid? TenantId,
        NotificationChannel Channel,
        DateTime BucketUtc,
        DateTime? PreviousCalculationUtc);
}
