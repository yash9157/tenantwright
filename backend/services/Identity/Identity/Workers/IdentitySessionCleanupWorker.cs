using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Workers;
using Shared.Uow.Abstractions;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.EntityFrameworkCore;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.Domain.Shared.ApplicationUsers;
using Shared.Caching.Sessions;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Identity.Workers;

/// <summary>
/// Periodically marks long-dead Active sessions as Expired and purges Expired/Revoked
/// sessions older than the configured retention age.
/// Registered through AddSharedPeriodicWorker.
/// </summary>
public sealed class IdentitySessionCleanupWorker : IPeriodicBackgroundWorker
{
    public const string WorkerName = "identity-session-cleanup";
    public const string OwnershipLockName = "identity:sessions:cleanup-owner";

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly IDataFilter _dataFilter;
    private readonly IOptionsMonitor<SessionValidationOptions> _options;
    private readonly ILogger<IdentitySessionCleanupWorker> _logger;

    public IdentitySessionCleanupWorker(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider,
        IUnitOfWorkExecutor unitOfWorkExecutor,
        IDataFilter dataFilter,
        IOptionsMonitor<SessionValidationOptions> options,
        ILogger<IdentitySessionCleanupWorker> logger)
    {
        _dbContextProvider = dbContextProvider;
        _unitOfWorkExecutor = unitOfWorkExecutor;
        _dataFilter = dataFilter;
        _options = options;
        _logger = logger;
    }

    public async Task<BackgroundWorkerExecutionResult> ExecuteAsync(BackgroundWorkerContext context)
    {
        var options = _options.CurrentValue;

        if (!options.CleanupEnabled)
        {
            _logger.LogDebug("Identity session cleanup worker is disabled.");
            return BackgroundWorkerExecutionResult.Idle;
        }

        return await _unitOfWorkExecutor.ExecuteTransactionalAsync(async cancellationToken =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            using var tenantFilter = _dataFilter.Disable<IMultiTenant>();

            var now = DateTime.UtcNow;
            var absoluteExpiryCutoff = now.AddHours(-options.MarkerTtlHours);

            // 1. Mark Active rows past their absolute lifetime as Expired. Idle expiry is
            // intentionally NOT decided here: LastActiveTime is only bumped by the
            // (rarely hit) DB validation path, not by the marker-refresh hot path that
            // other services use, so an actively-used session has a stale LastActiveTime
            // and would be falsely expired. Idle is enforced by the Valkey marker TTL and
            // surfaced at read time by ComputeEffectiveStatusAsync; the absolute lifetime
            // (effective creation time) is the only reliable DB-side signal.
            var deadSessions = await dbContext.IdentitySessions
                .Where(x => x.Status == IdentitySessionStatus.Active &&
                            x.CreationTime <= absoluteExpiryCutoff)
                .ToListAsync(cancellationToken);

            if (deadSessions.Count > 0)
            {
                _logger.LogInformation("Marking {Count} long-dead active sessions as Expired.", deadSessions.Count);
                foreach (var session in deadSessions)
                {
                    session.Status = IdentitySessionStatus.Expired;
                }
            }

            // 2. Purge Revoked/Expired rows older than the retention age
            var retentionCutoff = now.AddDays(-options.RetentionDays);
            var staleSessions = await dbContext.IdentitySessions
                .Where(x => (x.Status == IdentitySessionStatus.Revoked || x.Status == IdentitySessionStatus.Expired) &&
                            x.LastActiveTime <= retentionCutoff)
                .ToListAsync(cancellationToken);

            if (staleSessions.Count > 0)
            {
                _logger.LogInformation("Purging {Count} stale sessions older than {RetentionDays} days.", staleSessions.Count, options.RetentionDays);
                dbContext.IdentitySessions.RemoveRange(staleSessions);
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            return BackgroundWorkerExecutionResult.Idle;
        }, context.CancellationToken);
    }
}

public sealed class IdentitySessionCleanupWorkerOptions : BackgroundWorkerOptions
{
    public static IdentitySessionCleanupWorkerOptions FromSessionValidation(SessionValidationOptions options)
    {
        var intervalMs = checked((int)TimeSpan
            .FromSeconds(Math.Clamp(options.CleanupIntervalSeconds, 10, 86400))
            .TotalMilliseconds);

        return new IdentitySessionCleanupWorkerOptions
        {
            Enabled = options.CleanupEnabled,
            WorkerName = IdentitySessionCleanupWorker.WorkerName,
            PollIntervalMs = intervalMs,
            ErrorDelayMs = Math.Clamp(intervalMs, 1_000, 300_000),
            DistributedLockName = IdentitySessionCleanupWorker.OwnershipLockName,
            OwnershipAcquireTimeoutMs = 100,
            OwnershipLeaseMs = Math.Clamp(intervalMs, 1_000, 300_000)
        };
    }
}
