using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Data.ConnectionStrings;
using Shared.MultiTenancy;

namespace Shared.EntityFrameworkCore.TenantDatabases;

/// <summary>
/// Back-stop that migrates a dedicated tenant database tenant work found unmigrated, so a database whose
/// <c>TenantDatabaseChangedEto</c> was dead-lettered (or never arrived) converges without an operator or a restart.
/// <para>
/// <see cref="TenantDatabaseMigrationGuard"/> requests a (DbContext, tenant) pair when it blocks tenant work; the
/// request is idempotent while one is outstanding. Each pair is attempted after <see cref="InitialDelay"/> (giving an
/// in-flight event-driven migration time to finish) and then on an exponential back-off capped at
/// <see cref="MaximumDelay"/>, through <see cref="TenantDatabaseMigrator.ReconcileTenantAsync"/> and therefore under
/// the same per-database lock as every other trigger. Attempts run one at a time, so the host never migrates more than
/// one database concurrently on this path. Tenant work keeps failing closed until an attempt succeeds.
/// </para>
/// </summary>
public sealed class TenantDatabaseMigrationReconciler : BackgroundService
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<ReconciliationKey, ReconciliationState> _pending = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantDatabaseMigrationReconciler> _logger;

    public TenantDatabaseMigrationReconciler(
        IServiceScopeFactory scopeFactory,
        ILogger<TenantDatabaseMigrationReconciler> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Pairs currently awaiting a reconciliation attempt.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Schedules a migration attempt for <paramref name="tenantId"/>'s dedicated database of
    /// <paramref name="dbContextType"/>. Returns true when this call scheduled it, false when it was already pending or
    /// the arguments cannot name a dedicated database.
    /// </summary>
    public bool Request(Type dbContextType, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);
        if (tenantId == Guid.Empty || !TenantDatabaseMigrator.SupportsDedicatedDatabases(dbContextType))
        {
            return false;
        }

        return _pending.TryAdd(
            new ReconciliationKey(dbContextType, tenantId),
            new ReconciliationState(Attempts: 0, DueAt: _timeProvider.GetUtcNow() + InitialDelay));
    }

    /// <summary>
    /// Runs every attempt that is due now, one at a time, and returns how many databases were reconciled. The hosted
    /// loop calls this on a short poll; it is public so the schedule can be driven deterministically.
    /// </summary>
    public async Task<int> ReconcileDueAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var reconciled = 0;
        foreach (var (key, state) in _pending.Where(entry => entry.Value.DueAt <= now).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryReconcileAsync(key, state, cancellationToken))
            {
                reconciled++;
            }
        }

        return reconciled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ReconcileDueAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TryReconcileAsync(
        ReconciliationKey key,
        ReconciliationState state,
        CancellationToken cancellationToken)
    {
        var connectionStringName = ConnectionStringNameAttribute.GetConnStringName(key.DbContextType);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var migrated = await scope.ServiceProvider
                .GetRequiredService<TenantDatabaseMigrator>()
                .ReconcileTenantAsync(key.DbContextType, key.TenantId, cancellationToken);
            _pending.TryRemove(key, out _);
            if (migrated)
            {
                _logger.LogInformation(
                    "Reconciled a dedicated tenant database for {ConnectionName} after {Attempts} earlier attempts. TenantHash={TenantHash}.",
                    connectionStringName,
                    state.Attempts,
                    TenantResolutionHash.HashTenantId(key.TenantId));
            }

            return migrated;
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            var attempts = state.Attempts + 1;
            var delay = BackOff(attempts);
            _pending[key] = new ReconciliationState(attempts, _timeProvider.GetUtcNow() + delay);
            // Exception type only: provider messages can echo connection or parameter values.
            _logger.LogWarning(
                "Reconciling a dedicated tenant database for {ConnectionName} failed ({ErrorType}); its tenant keeps failing closed. Attempt={Attempt}, NextAttemptInSeconds={NextAttemptInSeconds}, TenantHash={TenantHash}.",
                connectionStringName,
                exception.GetType().Name,
                attempts,
                (int)delay.TotalSeconds,
                TenantResolutionHash.HashTenantId(key.TenantId));
            return false;
        }
    }

    /// <summary>30 s, 1 min, 2 min, 4 min, 8 min, then <see cref="MaximumDelay"/>.</summary>
    internal static TimeSpan BackOff(int failedAttempts)
    {
        var exponent = Math.Clamp(failedAttempts - 1, 0, 10);
        var delay = TimeSpan.FromSeconds(30 * Math.Pow(2, exponent));
        return delay < MaximumDelay ? delay : MaximumDelay;
    }

    private readonly record struct ReconciliationKey(Type DbContextType, Guid TenantId);

    private readonly record struct ReconciliationState(int Attempts, DateTimeOffset DueAt);
}
