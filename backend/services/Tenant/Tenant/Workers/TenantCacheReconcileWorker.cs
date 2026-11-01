using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Workers;
using Shared.Uow.Abstractions;
using Tenant.Application.Tenants;

namespace Tenant.Workers;

/// <summary>
/// Periodically rebuilds the tenant Valkey cache (by-id, by-name, list) from the
/// Tenant database via <see cref="TenantCacheManager.RefreshAllAsync"/>. This is
/// the DB-&gt;Valkey reconcile path: if Valkey is flushed or a write is lost, the
/// cache self-heals within <c>TenantCache:ReconcileIntervalSeconds</c> instead of
/// requiring a service restart to trigger <c>WarmupAsync</c> again. It is
/// registered through <c>AddSharedPeriodicWorker</c>, which provides the
/// cross-replica ownership lock, telemetry, cancellation, and retry delay.
/// Runs only in the Tenant host, since <see cref="TenantCacheManager"/> is the
/// only writer of these cache entries.
/// </summary>
public sealed class TenantCacheReconcileWorker : IPeriodicBackgroundWorker
{
    public const string WorkerName = "tenant-cache-reconcile";
    public const string OwnershipLockName = "tenant:cache:reconcile-owner";

    private readonly TenantCacheManager _cacheManager;
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly IOptionsMonitor<TenantCacheOptions> _options;
    private readonly ILogger<TenantCacheReconcileWorker> _logger;

    public TenantCacheReconcileWorker(
        TenantCacheManager cacheManager,
        IUnitOfWorkExecutor unitOfWorkExecutor,
        IOptionsMonitor<TenantCacheOptions> options,
        ILogger<TenantCacheReconcileWorker> logger)
    {
        _cacheManager = cacheManager;
        _unitOfWorkExecutor = unitOfWorkExecutor;
        _options = options;
        _logger = logger;
    }

    public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
    {
        var options = _options.CurrentValue;

        if (!options.ReconcileEnabled)
        {
            _logger.LogDebug("Tenant cache reconcile worker is disabled.");
            return BackgroundWorkerExecutionResult.Idle;
        }

        await _unitOfWorkExecutor.ExecuteNonTransactionalAsync(
            token => _cacheManager.RefreshAllAsync(token),
            context.CancellationToken);
        return BackgroundWorkerExecutionResult.Idle;
    }
}

public sealed class TenantCacheReconcileWorkerOptions : BackgroundWorkerOptions
{
    public static TenantCacheReconcileWorkerOptions FromTenantCache(TenantCacheOptions options)
    {
        var intervalMs = checked((int)TimeSpan
            .FromSeconds(Math.Clamp(options.ReconcileIntervalSeconds, 10, 86400))
            .TotalMilliseconds);

        return new TenantCacheReconcileWorkerOptions
        {
            Enabled = options.ReconcileEnabled,
            WorkerName = TenantCacheReconcileWorker.WorkerName,
            PollIntervalMs = intervalMs,
            ErrorDelayMs = Math.Clamp(intervalMs, 1_000, 300_000),
            DistributedLockName = TenantCacheReconcileWorker.OwnershipLockName,
            OwnershipAcquireTimeoutMs = 100,
            OwnershipLeaseMs = Math.Clamp(intervalMs, 1_000, 300_000)
        };
    }
}
