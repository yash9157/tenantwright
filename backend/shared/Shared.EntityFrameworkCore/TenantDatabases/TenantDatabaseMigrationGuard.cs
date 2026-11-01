using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Shared.EntityFrameworkCore.TenantDatabases;

/// <summary>
/// Fails closed when tenant work reaches a dedicated database whose schema is behind the compiled migrations (or
/// belongs to another owner), instead of letting EF surface missing-table errors (or, worse, partially succeed)
/// against a half-provisioned database.
/// <para>
/// The first use of each (DbContext, database) pair in a process reads the catalog and <c>__EFMigrationsHistory</c>;
/// once fully migrated it is remembered for the process lifetime, so the check costs one round-trip per database per
/// process. Pending migrations are never applied here: request paths have no DDL authority. The
/// <see cref="TenantDatabaseMigrator"/> (startup, migrate-and-exit, <c>TenantDatabaseChangedEto</c>) applies them and
/// marks the database verified; a database found unmigrated is handed to the
/// <see cref="TenantDatabaseMigrationReconciler"/>, so it converges even when its change event was lost.
/// </para>
/// <para>
/// An unmigrated database is an expected state while a migration is in flight or being retried, so it is reported as a
/// warning once per database per <see cref="ReportInterval"/>, not on every blocked unit of work; every block is counted
/// by <see cref="TenantDatabaseMetrics.RecordNotMigrated"/>.
/// </para>
/// </summary>
public sealed class TenantDatabaseMigrationGuard : ISingletonDependency
{
    /// <summary>Minimum time between two log reports of the same blocked database.</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, byte> _verified = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastReported = new(StringComparer.Ordinal);
    private readonly TenantDatabaseMigrationReconciler? _reconciler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantDatabaseMigrationGuard> _logger;

    public TenantDatabaseMigrationGuard(
        ILogger<TenantDatabaseMigrationGuard> logger,
        TenantDatabaseMigrationReconciler? reconciler = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _reconciler = reconciler;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task EnsureMigratedAsync(
        DbContext dbContext,
        TenantDatabaseConnection connection,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(connection);

        var key = BuildKey(dbContext.GetType(), connection.ConnectionString);
        if (_verified.ContainsKey(key))
        {
            return;
        }

        var schema = await TenantDatabaseSchemaInspector.InspectAsync(dbContext, cancellationToken);
        if (!schema.HoldsForeignSchema && schema.PendingMigrationCount == 0)
        {
            _verified.TryAdd(key, 0);
            _lastReported.TryRemove(key, out _);
            return;
        }

        TenantDatabaseMetrics.RecordNotMigrated(connection.ConnectionStringName);
        var reconciliationScheduled = _reconciler?.Request(dbContext.GetType(), tenantId) ?? false;
        if (ShouldReport(key))
        {
            _logger.LogWarning(
                "Dedicated tenant database for {ConnectionName} is not migrated (PendingMigrations={PendingMigrationCount}, ForeignMigrations={ForeignMigrationCount}, UnmanagedRelations={UnmanagedRelationCount}); tenant work fails closed until it is. ReconciliationScheduled={ReconciliationScheduled}, TenantHash={TenantHash}.",
                connection.ConnectionStringName,
                schema.PendingMigrationCount,
                schema.ForeignMigrationCount,
                schema.UnmanagedRelationCount,
                reconciliationScheduled,
                TenantResolutionHash.HashTenantId(tenantId));
        }

        if (schema.HoldsForeignSchema)
        {
            throw new TenantDatabaseForeignSchemaException(connection.ConnectionStringName, schema);
        }

        throw new TenantDatabaseNotMigratedException(connection.ConnectionStringName, schema.PendingMigrationCount);
    }

    /// <summary>Records a database the migrator just brought up to date.</summary>
    public void MarkMigrated(Type dbContextType, string connectionString)
    {
        var key = BuildKey(dbContextType, connectionString);
        _verified.TryAdd(key, 0);
        _lastReported.TryRemove(key, out _);
    }

    /// <summary>Forgets a database so its next use is verified again (its assignment changed).</summary>
    public void Forget(Type dbContextType, string connectionString) =>
        _verified.TryRemove(BuildKey(dbContextType, connectionString), out _);

    private bool ShouldReport(string key)
    {
        var now = _timeProvider.GetUtcNow();
        while (true)
        {
            if (!_lastReported.TryGetValue(key, out var last))
            {
                if (_lastReported.TryAdd(key, now))
                {
                    return true;
                }

                continue;
            }

            if (now - last < ReportInterval)
            {
                return false;
            }

            if (_lastReported.TryUpdate(key, now, last))
            {
                return true;
            }
        }
    }

    private static string BuildKey(Type dbContextType, string connectionString) =>
        $"{dbContextType.FullName}|{PostgreSqlDatabaseTarget.FingerprintOf(connectionString)}";
}

/// <summary>
/// A dedicated tenant database is not migrated to the compiled model. This is an operator/provisioning failure, not a
/// caller-correctable request error; the message carries only the catalog name and the pending migration count.
/// </summary>
public sealed class TenantDatabaseNotMigratedException : InvalidOperationException
{
    public TenantDatabaseNotMigratedException(string connectionStringName, int pendingMigrationCount)
        : base(
            $"The dedicated tenant database for '{connectionStringName}' has {pendingMigrationCount} pending " +
            "migrations; tenant work is blocked until it is migrated.")
    {
        ConnectionStringName = connectionStringName;
        PendingMigrationCount = pendingMigrationCount;
    }

    public string ConnectionStringName { get; }

    public int PendingMigrationCount { get; }
}
