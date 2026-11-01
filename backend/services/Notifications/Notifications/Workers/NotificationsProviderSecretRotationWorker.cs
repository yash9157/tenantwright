using Microsoft.Extensions.Options;
using Notifications.Application.ProviderConfig.Security;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.BackgroundTasks.Workers;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace Notifications.Workers;

public sealed class NotificationsProviderSecretRotationWorker : IPeriodicBackgroundWorker
{
    public const string WorkerName = "notifications-provider-secret-rotation";
    public const string OwnershipLockName = "notifications:provider-secret-rotation-worker:owner";

    private readonly NotificationsProviderSecretRewrapService _service;
    private readonly TenantDatabaseFanOut _fanOut;
    private readonly IOptionsMonitor<NotificationsProviderSecretRotationOptions> _options;

    public NotificationsProviderSecretRotationWorker(
        NotificationsProviderSecretRewrapService service,
        TenantDatabaseFanOut fanOut,
        IOptionsMonitor<NotificationsProviderSecretRotationOptions> options)
    {
        _service = service;
        _fanOut = fanOut;
        _options = options;
    }

    public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return BackgroundWorkerExecutionResult.Idle;
        }

        // Provider secrets are re-wrapped wherever they are stored, so a retired key can be removed only after every
        // Notifications database (shared and dedicated) has moved off it.
        var batchSize = Math.Clamp(options.BatchSize, 1, 1000);
        await _fanOut.ForEachDatabaseAsync<NotificationsDbContextApp>(
            WorkerName,
            (_, cancellationToken) => _service.RewrapBatchAsync(batchSize, cancellationToken),
            context.CancellationToken);
        return BackgroundWorkerExecutionResult.Idle;
    }
}
