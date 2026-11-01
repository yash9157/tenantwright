using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Permissions.Application.Grants;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.BackgroundTasks.Workers;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace Permissions.Workers;

/// <summary>
/// Periodically hard-deletes expired <c>PermissionGrant</c> rows via <see cref="PermissionGrantSweepService"/>
/// and bumps the affected providers' stamps so cached snapshots invalidate promptly rather than waiting
/// out the snapshot cache TTL. Registered through <c>AddSharedPeriodicWorker</c>, which provides the
/// cross-replica ownership lock, telemetry, cancellation, and retry delay. Sweeps every Permissions database: the
/// shared one in host context (every shared tenant's expired grants in one pass) and each dedicated tenant
/// database in its tenant's scope.
/// </summary>
public sealed class PermissionGrantSweepWorker : IPeriodicBackgroundWorker
{
    public const string WorkerName = "permissions-grant-sweep";
    public const string OwnershipLockName = "permissions:grant-sweep:owner";

    private readonly PermissionGrantSweepService _sweepService;
    private readonly TenantDatabaseFanOut _fanOut;
    private readonly IOptionsMonitor<PermissionGrantSweepOptions> _options;
    private readonly ILogger<PermissionGrantSweepWorker> _logger;

    public PermissionGrantSweepWorker(
        PermissionGrantSweepService sweepService,
        TenantDatabaseFanOut fanOut,
        IOptionsMonitor<PermissionGrantSweepOptions> options,
        ILogger<PermissionGrantSweepWorker> logger)
    {
        _sweepService = sweepService;
        _fanOut = fanOut;
        _options = options;
        _logger = logger;
    }

    public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
    {
        if (!_options.CurrentValue.Enabled)
        {
            _logger.LogDebug("Permission grant sweep worker is disabled.");
            return BackgroundWorkerExecutionResult.Idle;
        }

        await _fanOut.ForEachDatabaseAsync<PermissionsDbContextApp, int>(
            WorkerName,
            (_, cancellationToken) => _sweepService.SweepAsync(cancellationToken),
            context.CancellationToken);
        return BackgroundWorkerExecutionResult.Idle;
    }
}

public sealed class PermissionGrantSweepWorkerOptions : BackgroundWorkerOptions
{
    public static PermissionGrantSweepWorkerOptions FromSweepOptions(PermissionGrantSweepOptions options)
    {
        var intervalMs = checked((int)TimeSpan
            .FromSeconds(Math.Clamp(options.IntervalSeconds, 60, 86_400))
            .TotalMilliseconds);

        return new PermissionGrantSweepWorkerOptions
        {
            Enabled = options.Enabled,
            WorkerName = PermissionGrantSweepWorker.WorkerName,
            PollIntervalMs = intervalMs,
            ErrorDelayMs = Math.Clamp(intervalMs, 1_000, 300_000),
            DistributedLockName = PermissionGrantSweepWorker.OwnershipLockName,
            OwnershipAcquireTimeoutMs = 100,
            OwnershipLeaseMs = Math.Clamp(intervalMs, 1_000, 300_000)
        };
    }
}
