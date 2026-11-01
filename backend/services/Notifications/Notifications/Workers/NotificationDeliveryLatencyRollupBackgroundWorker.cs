using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Providers;
using Notifications.Application.Delivery;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.BackgroundTasks.Workers;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace Notifications.Workers;

public sealed class NotificationDeliveryLatencyRollupBackgroundWorker : IPeriodicBackgroundWorker
{
    public const string WorkerName = "notifications-delivery-latency-rollup";
    public const string OwnershipLockName = "notifications:delivery-latency-rollup:owner";

    private readonly INotificationDeliveryLatencyRollupProcessor _processor;
    private readonly TenantDatabaseFanOut _fanOut;
    private readonly IOptionsMonitor<NotificationsDeliveryLatencyRollupOptions> _options;
    private readonly ILogger<NotificationDeliveryLatencyRollupBackgroundWorker> _logger;

    public NotificationDeliveryLatencyRollupBackgroundWorker(
        INotificationDeliveryLatencyRollupProcessor processor,
        TenantDatabaseFanOut fanOut,
        IOptionsMonitor<NotificationsDeliveryLatencyRollupOptions> options,
        ILogger<NotificationDeliveryLatencyRollupBackgroundWorker> logger)
    {
        _processor = processor;
        _fanOut = fanOut;
        _options = options;
        _logger = logger;
    }

    public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
    {
        // Rollups live next to the deliveries they summarize: every Notifications database is rolled up in place.
        var batches = await _fanOut.ForEachDatabaseAsync<NotificationsDbContextApp, NotificationDeliveryLatencyRollupBatchResult>(
            WorkerName,
            (_, cancellationToken) => _processor.RollupBatchAsync(cancellationToken),
            context.CancellationToken);
        var processedBucketCount = batches.Sum(batch => batch.ProcessedBucketCount);
        if (processedBucketCount > 0)
        {
            _logger.LogDebug(
                "Notification delivery latency rollup refreshed {BucketCount} bucket(s).",
                processedBucketCount);
        }

        if (batches.Any(batch => batch.HasMoreUnrolledBuckets))
        {
            return BackgroundWorkerExecutionResult.WorkCompleted;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.CurrentValue.IntervalSeconds, 30, 3600));
        return BackgroundWorkerExecutionResult.Wait(cancellationToken => Task.Delay(interval, cancellationToken));
    }
}
