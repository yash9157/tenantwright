using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Retention;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.BackgroundTasks.Workers;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace Notifications.Workers;

/// <summary>
/// Periodically hard-deletes past-retention Notifications data via
/// <see cref="NotificationRetentionService"/>. Registered through
/// <c>AddSharedPeriodicWorker</c>, which provides the cross-replica ownership lock,
/// telemetry, cancellation, and retry delay. Purges every Notifications database: the shared one in host context and
/// each dedicated tenant database in its tenant's scope.
/// </summary>
public sealed class NotificationsRetentionWorker : IPeriodicBackgroundWorker
{
    public const string WorkerName = "notifications-retention";
    public const string OwnershipLockName = "notifications:retention-worker:owner";

    private readonly NotificationRetentionService _retentionService;
    private readonly TenantDatabaseFanOut _fanOut;
    private readonly IOptionsMonitor<NotificationsRetentionOptions> _options;
    private readonly ILogger<NotificationsRetentionWorker> _logger;

    public NotificationsRetentionWorker(
        NotificationRetentionService retentionService,
        TenantDatabaseFanOut fanOut,
        IOptionsMonitor<NotificationsRetentionOptions> options,
        ILogger<NotificationsRetentionWorker> logger)
    {
        _retentionService = retentionService;
        _fanOut = fanOut;
        _options = options;
        _logger = logger;
    }

    public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
    {
        if (!_options.CurrentValue.Enabled)
        {
            _logger.LogDebug("Notifications retention worker is disabled.");
            return BackgroundWorkerExecutionResult.Idle;
        }

        await _fanOut.ForEachDatabaseAsync<NotificationsDbContextApp>(
            WorkerName,
            (_, cancellationToken) => _retentionService.PurgeAsync(cancellationToken),
            context.CancellationToken);
        return BackgroundWorkerExecutionResult.Idle;
    }
}

public sealed class NotificationsRetentionWorkerOptions : BackgroundWorkerOptions
{
    public static NotificationsRetentionWorkerOptions FromRetention(NotificationsRetentionOptions options)
    {
        var intervalMs = checked((int)TimeSpan
            .FromSeconds(Math.Clamp(options.IntervalSeconds, 60, 86_400))
            .TotalMilliseconds);

        return new NotificationsRetentionWorkerOptions
        {
            Enabled = options.Enabled,
            WorkerName = NotificationsRetentionWorker.WorkerName,
            PollIntervalMs = intervalMs,
            ErrorDelayMs = Math.Clamp(intervalMs, 1_000, 300_000),
            DistributedLockName = NotificationsRetentionWorker.OwnershipLockName,
            OwnershipAcquireTimeoutMs = 100,
            OwnershipLeaseMs = Math.Clamp(intervalMs, 1_000, 300_000)
        };
    }
}
