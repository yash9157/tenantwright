using Microsoft.Extensions.Options;
using Notifications.Application.Digests;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.BackgroundTasks.Workers;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace Notifications.Workers;

public sealed class NotificationsDigestWorker : IPeriodicBackgroundWorker
{
    public const string WorkerName = "notifications-digest";
    public const string OwnershipLockName = "notifications:digest-worker:owner";

    private readonly NotificationDigestProcessor _processor;
    private readonly TenantDatabaseFanOut _fanOut;
    private readonly IOptionsMonitor<NotificationsDigestOptions> _options;

    public NotificationsDigestWorker(
        NotificationDigestProcessor processor,
        TenantDatabaseFanOut fanOut,
        IOptionsMonitor<NotificationsDigestOptions> options)
    {
        _processor = processor;
        _fanOut = fanOut;
        _options = options;
    }

    public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return BackgroundWorkerExecutionResult.Idle;
        }

        await _fanOut.ForEachDatabaseAsync<NotificationsDbContextApp, int>(
            WorkerName,
            (_, cancellationToken) => _processor.ProcessBatchAsync(cancellationToken),
            context.CancellationToken);
        return BackgroundWorkerExecutionResult.Idle;
    }
}
