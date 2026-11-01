using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.BackgroundTasks.Workers;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace Notifications.Delivery;

public sealed class NotificationDeliveryBackgroundWorker : IPeriodicBackgroundWorker
{
    public const string WorkerName = "notifications-delivery";
    public const string OwnershipLockName = "notifications:delivery-worker:owner";

    private readonly INotificationDeliveryWorker _worker;
    private readonly TenantDatabaseFanOut _fanOut;
    private readonly IBackgroundWorkerWakeSignal _wakeSignal;
    private readonly IOptionsMonitor<NotificationsDeliveryOptions> _options;
    private readonly ILogger<NotificationDeliveryBackgroundWorker> _logger;

    public NotificationDeliveryBackgroundWorker(
        INotificationDeliveryWorker worker,
        TenantDatabaseFanOut fanOut,
        IBackgroundWorkerWakeSignal wakeSignal,
        IOptionsMonitor<NotificationsDeliveryOptions> options,
        ILogger<NotificationDeliveryBackgroundWorker> logger)
    {
        _worker = worker;
        _fanOut = fanOut;
        _wakeSignal = wakeSignal;
        _options = options;
        _logger = logger;
    }

    public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
    {
        // Deliveries are claimed from every Notifications database, each in its owning tenant's scope.
        var processed = await _fanOut.ForEachDatabaseAsync<NotificationsDbContextApp, int>(
            WorkerName,
            (_, cancellationToken) => _worker.ProcessBatchAsync(cancellationToken),
            context.CancellationToken);
        if (processed.Sum() > 0)
        {
            return BackgroundWorkerExecutionResult.WorkCompleted;
        }

        return BackgroundWorkerExecutionResult.Wait(async cancellationToken =>
        {
            var wakeResult = await _wakeSignal.WaitAsync(
                NotificationDeliveryWakeKeys.Delivery,
                NotificationDeliveryWakeKeys.CreateWakeOptions(_options.CurrentValue),
                cancellationToken);
            _logger.LogDebug(
                "Notification delivery worker woke by {WakeReason}.",
                wakeResult == BackgroundWorkerWakeResult.Signal ? "signal" : "safety_poll");
        });
    }
}
