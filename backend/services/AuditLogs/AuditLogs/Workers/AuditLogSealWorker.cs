using AuditLogs.Application.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Workers;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace AuditLogs.Workers;

/// <summary>
/// Periodically advances the <see cref="AuditLogSealService"/> hash chain (sub-flow 1b: at-rest
/// tamper-evidence, deletion/reordering detection). Registered through <c>AddSharedPeriodicWorker</c>,
/// which provides the cross-replica ownership lock so only one replica seals at a time. Visits every
/// AuditLogs database: the shared one in host context, where the chain covers its single global
/// <c>AuditLog.Id</c> sequence across shared tenants, and each dedicated tenant database in that tenant's
/// scope, where an independent chain covers that database's own sequence.
/// </summary>
public sealed class AuditLogSealWorker : IPeriodicBackgroundWorker
{
    public const string WorkerName = "auditlogs-seal-worker";
    public const string OwnershipLockName = "auditlogs:seal-worker:owner";

    private readonly AuditLogSealService _sealService;
    private readonly TenantDatabaseFanOut _fanOut;
    private readonly IOptionsMonitor<AuditLogSealOptions> _options;
    private readonly ILogger<AuditLogSealWorker> _logger;

    public AuditLogSealWorker(
        AuditLogSealService sealService,
        TenantDatabaseFanOut fanOut,
        IOptionsMonitor<AuditLogSealOptions> options,
        ILogger<AuditLogSealWorker> logger)
    {
        _sealService = sealService;
        _fanOut = fanOut;
        _options = options;
        _logger = logger;
    }

    public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
    {
        if (!_options.CurrentValue.Enabled)
        {
            _logger.LogDebug("Audit log seal worker is disabled.");
            return BackgroundWorkerExecutionResult.Idle;
        }

        // Each database keeps its own chain over its own AuditLog.Id sequence: the shared database in host
        // scope, every dedicated tenant database in its tenant's scope.
        var sealCreated = await _fanOut.ForEachDatabaseAsync<AuditLogsDbContextApp, bool>(
            WorkerName,
            (_, cancellationToken) => _sealService.SealNextBatchAsync(cancellationToken),
            context.CancellationToken);

        // If we sealed a batch anywhere, there may be more eligible rows waiting right behind it - run
        // again immediately instead of waiting a full poll interval. Otherwise, idle until next tick.
        return sealCreated.Any(created => created)
            ? BackgroundWorkerExecutionResult.WorkCompleted
            : BackgroundWorkerExecutionResult.Idle;
    }
}

public sealed class AuditLogSealWorkerOptions : BackgroundWorkerOptions
{
    public static AuditLogSealWorkerOptions FromSealOptions(AuditLogSealOptions options)
    {
        var intervalMs = checked((int)TimeSpan
            .FromSeconds(Math.Clamp(options.IntervalSeconds, 10, 3600))
            .TotalMilliseconds);

        return new AuditLogSealWorkerOptions
        {
            Enabled = options.Enabled,
            WorkerName = AuditLogSealWorker.WorkerName,
            PollIntervalMs = intervalMs,
            ErrorDelayMs = Math.Clamp(intervalMs, 1_000, 300_000),
            DistributedLockName = AuditLogSealWorker.OwnershipLockName,
            OwnershipAcquireTimeoutMs = 100,
            OwnershipLeaseMs = Math.Clamp(intervalMs * 2, 1_000, 300_000)
        };
    }
}
