using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Data.ConnectionStrings;
using Shared.Data.Seeding;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;
using Shared.EntityFrameworkCore.ConnectionStrings;
using Shared.EntityFrameworkCore.Migrations;
using Shared.EventBus.Messaging;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Options;

namespace Shared.EntityFrameworkCore.TenantDatabases;

/// <summary>
/// Applies EF migrations to dedicated tenant databases and then re-runs the service's idempotent data seeders in
/// that tenant's scope. <see cref="SharedEfCoreAutoMigrator"/> owns the shared database; this type owns every
/// dedicated one, for the same DbContexts (<see cref="SharedEfCoreAutoMigrationOptions.DbContextTypes"/>).
/// <para>
/// Triggers: application startup when <c>EfCore:AutoMigrate</c> is enabled (including the <c>EfCore:MigrateAndExit</c>
/// job), <c>TenantDatabaseChangedEto</c> after the Tenant service assigns or re-points a dedicated database, and the
/// <see cref="TenantDatabaseMigrationReconciler"/> for a database tenant work found unmigrated. Each (tenant, DbContext)
/// pair is serialized by a distributed lock, so replicas, triggers and redeliveries never migrate or seed the same
/// database concurrently; EF's migration history makes repeated runs no-ops.
/// </para>
/// <para>
/// The migration never joins the caller's unit of work: the DbContext is activated from a fresh service scope with its
/// own connection (the inbox transaction a message handler runs in stays on the shared database), and EF applies every
/// pending migration together with its history rows in one transaction of its own, so an interrupted or failed
/// attempt leaves nothing behind and is safely retried. A database that already holds another owner's schema is
/// refused before any DDL (<see cref="TenantDatabaseForeignSchemaException"/>). Seeding runs afterwards in its own
/// transactional unit of work.
/// </para>
/// </summary>
public sealed class TenantDatabaseMigrator : ITransientDependency
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<SharedEfCoreAutoMigrationOptions> _options;
    private readonly ITenantDatabaseTopology _topology;
    private readonly TenantDatabaseMigrationGuard _guard;
    private readonly ITenantConnectionStringsCache _connectionStringsCache;
    private readonly ITenantStore _tenantStore;
    private readonly IDistributedLock _distributedLock;
    private readonly ICurrentTenant _currentTenant;
    private readonly ILogger<TenantDatabaseMigrator> _logger;

    public TenantDatabaseMigrator(
        IServiceScopeFactory scopeFactory,
        IOptions<SharedEfCoreAutoMigrationOptions> options,
        ITenantDatabaseTopology topology,
        TenantDatabaseMigrationGuard guard,
        ITenantConnectionStringsCache connectionStringsCache,
        ITenantStore tenantStore,
        IDistributedLock distributedLock,
        ICurrentTenant currentTenant,
        ILogger<TenantDatabaseMigrator> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _topology = topology;
        _guard = guard;
        _connectionStringsCache = connectionStringsCache;
        _tenantStore = tenantStore;
        _distributedLock = distributedLock;
        _currentTenant = currentTenant;
        _logger = logger;
    }

    /// <summary>
    /// Migrates and seeds every usable dedicated database of every registered DbContext. Failures of individual
    /// databases are counted and logged, never thrown, so one broken tenant database cannot block startup of the
    /// others; the caller decides whether an incomplete or failed report is fatal.
    /// </summary>
    public async Task<TenantDatabaseMigrationReport> MigrateAllDedicatedAsync(CancellationToken cancellationToken = default)
    {
        _topology.Invalidate();
        var migrated = 0;
        var failed = 0;
        var incomplete = false;
        foreach (var dbContextType in GetDedicatableDbContextTypes())
        {
            var topology = await _topology.GetSnapshotAsync(dbContextType, cancellationToken);
            incomplete |= !topology.IsComplete;
            failed += topology.ConflictingTenantIds.Count + topology.UnresolvableTenantIds.Count;

            foreach (var database in topology.Dedicated)
            {
                try
                {
                    await MigrateAndSeedAsync(dbContextType, database.TenantId!.Value, "startup", cancellationToken);
                    migrated++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException ||
                                                  !cancellationToken.IsCancellationRequested)
                {
                    failed++;
                    TenantDatabaseMetrics.RecordMigration(topology.ConnectionStringName, "startup", "failed");
                    _logger.LogWarning(
                        "Migrating a dedicated tenant database for {ConnectionName} failed at startup ({ErrorType}); its tenant fails closed until it is migrated. DatabaseKey={DatabaseKey}.",
                        topology.ConnectionStringName,
                        exception.GetType().Name,
                        database.LogKey);
                }
            }
        }

        return new TenantDatabaseMigrationReport(migrated, failed, incomplete);
    }

    /// <summary>
    /// Brings one tenant's dedicated database for <paramref name="connectionStringName"/> up to date after its
    /// assignment changed. <paramref name="expectedTargetFingerprint"/> is the fingerprint the Tenant service
    /// committed (null when the assignment was removed); if this service still resolves a different target, the tenant
    /// cache has not converged yet and the call throws so messaging redelivers it.
    /// Returns false when this service does not own <paramref name="connectionStringName"/> or nothing is dedicated.
    /// </summary>
    public async Task<bool> MigrateTenantAsync(
        Guid tenantId,
        string connectionStringName,
        string? expectedTargetFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || !TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(connectionStringName))
        {
            return false;
        }

        var dbContextTypes = GetDedicatableDbContextTypes()
            .Where(type => string.Equals(
                ConnectionStringNameAttribute.GetConnStringName(type),
                connectionStringName,
                StringComparison.Ordinal))
            .ToArray();
        if (dbContextTypes.Length == 0)
        {
            return false;
        }

        // The assignment just changed: drop every in-process view of it before resolving again.
        await _connectionStringsCache.RemoveAsync(tenantId, cancellationToken);
        _topology.Invalidate();

        var tenant = await _tenantStore.FindAsync(tenantId, cancellationToken)
            ?? throw new TenantDatabaseAssignmentNotVisibleException(connectionStringName);
        if (!tenant.IsActive)
        {
            // Inactive tenants cannot resolve any database. Their dedicated database is migrated by the next
            // startup migration after reactivation; until then every request fails closed anyway.
            _logger.LogInformation(
                "Skipped migrating a dedicated tenant database for {ConnectionName}: the tenant is inactive. TenantHash={TenantHash}.",
                connectionStringName,
                TenantResolutionHash.HashTenantId(tenantId));
            return false;
        }

        var migrated = false;
        foreach (var dbContextType in dbContextTypes)
        {
            var connection = await ResolveForTenantAsync(dbContextType, tenantId);
            var actualFingerprint = connection.IsTenantDedicated
                ? PostgreSqlDatabaseTarget.FingerprintOf(connection.ConnectionString)
                : null;
            if (!string.Equals(actualFingerprint, expectedTargetFingerprint, StringComparison.Ordinal))
            {
                throw new TenantDatabaseAssignmentNotVisibleException(connectionStringName);
            }

            if (!connection.IsTenantDedicated)
            {
                continue;
            }

            await EnsureNotConflictingAsync(dbContextType, tenantId, connectionStringName, "database_changed", cancellationToken);
            await MigrateAndSeedAsync(dbContextType, tenantId, "database_changed", cancellationToken);
            migrated = true;
        }

        return migrated;
    }

    /// <summary>
    /// Back-stop for a dedicated database tenant work found unmigrated (for example after its change event was
    /// dead-lettered): migrates and seeds it under the same lock as every other trigger, against the assignment this
    /// service currently resolves. Returns false when the tenant is unknown or inactive, the DbContext is not one this
    /// host migrates, or the tenant no longer resolves a dedicated database.
    /// </summary>
    public async Task<bool> ReconcileTenantAsync(Type dbContextType, Guid tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);
        if (tenantId == Guid.Empty || !GetDedicatableDbContextTypes().Contains(dbContextType))
        {
            return false;
        }

        var tenant = await _tenantStore.FindAsync(tenantId, cancellationToken);
        if (tenant is not { IsActive: true })
        {
            return false;
        }

        var connectionStringName = ConnectionStringNameAttribute.GetConnStringName(dbContextType);
        await EnsureNotConflictingAsync(dbContextType, tenantId, connectionStringName, "reconcile", cancellationToken);
        return await MigrateAndSeedAsync(dbContextType, tenantId, "reconcile", cancellationToken);
    }

    private async Task EnsureNotConflictingAsync(
        Type dbContextType,
        Guid tenantId,
        string connectionStringName,
        string trigger,
        CancellationToken cancellationToken)
    {
        var topology = await _topology.GetSnapshotAsync(dbContextType, cancellationToken);
        if (topology.ConflictingTenantIds.Contains(tenantId))
        {
            TenantDatabaseMetrics.RecordMigration(connectionStringName, trigger, "conflict");
            throw new TenantDatabaseConflictException(connectionStringName);
        }
    }

    private async Task<bool> MigrateAndSeedAsync(
        Type dbContextType,
        Guid tenantId,
        string trigger,
        CancellationToken cancellationToken)
    {
        IDistributedLockHandle migrationLock;
        try
        {
            migrationLock = await _distributedLock.AcquireAsync(
                EfCoreDistributedLockNameFactory.BuildTenantDatabaseMigrationLockName(dbContextType, tenantId),
                new DistributedLockAcquireOptions
                {
                    PolicyName = DistributedLockPolicyNames.EfCoreMigration,
                    IgnoreTenant = true
                },
                cancellationToken);
        }
        catch (TimeoutException)
        {
            // Another replica or trigger is migrating this database right now.
            TenantDatabaseMetrics.RecordMigration(ConnectionStringNameAttribute.GetConnStringName(dbContextType), trigger, "busy");
            throw new TenantDatabaseMigrationBusyException(ConnectionStringNameAttribute.GetConnStringName(dbContextType));
        }

        await using (migrationLock)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            using (_currentTenant.Change(tenantId))
            {
                var connection = await TenantDatabaseDbContextActivator.ResolveConnectionAsync(
                    dbContextType,
                    scope.ServiceProvider.GetRequiredService<IConnectionStringResolver>(),
                    _currentTenant);
                if (!connection.IsTenantDedicated)
                {
                    return false;
                }

                var dbContext = (DbContext)TenantDatabaseDbContextActivator.Activate(
                    scope.ServiceProvider,
                    dbContextType,
                    connection,
                    out _);
                await using (dbContext)
                {
                    await MigrateOwnedDatabaseAsync(dbContext, connection, tenantId, trigger, cancellationToken);
                }

                _guard.MarkMigrated(dbContextType, connection.ConnectionString);
                TenantDatabaseMetrics.RecordMigration(connection.ConnectionStringName, trigger, "migrated");

                var seeder = scope.ServiceProvider.GetService<IDataSeeder>();
                if (seeder != null)
                {
                    await seeder.SeedInSeparateUowAsync(
                        tenantId,
                        new UnitOfWorkOptions { IsTransactional = true },
                        requiresNew: true);
                }

                return true;
            }
        }
    }

    private async Task MigrateOwnedDatabaseAsync(
        DbContext dbContext,
        TenantDatabaseConnection connection,
        Guid tenantId,
        string trigger,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Dedicated tenant database migrations must run on their own connection, outside any transaction.");
        }

        var schema = await TenantDatabaseSchemaInspector.InspectAsync(dbContext, cancellationToken);
        if (schema.HoldsForeignSchema)
        {
            TenantDatabaseMetrics.RecordMigration(connection.ConnectionStringName, trigger, "foreign_schema");
            _logger.LogError(
                "Refused to migrate a dedicated tenant database for {DbContext}: it already holds schema this service did not create (ForeignMigrations={ForeignMigrationCount}, UnmanagedRelations={UnmanagedRelationCount}). Assign an empty database; the tenant fails closed until then. TenantHash={TenantHash}.",
                dbContext.GetType().FullName,
                schema.ForeignMigrationCount,
                schema.UnmanagedRelationCount,
                TenantResolutionHash.HashTenantId(tenantId));
            throw new TenantDatabaseForeignSchemaException(connection.ConnectionStringName, schema);
        }

        if (schema.PendingMigrationCount == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Applying {PendingMigrationCount} EF Core migrations to a dedicated tenant database for {DbContext}. TenantHash={TenantHash}.",
            schema.PendingMigrationCount,
            dbContext.GetType().FullName,
            TenantResolutionHash.HashTenantId(tenantId));
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    private async Task<TenantDatabaseConnection> ResolveForTenantAsync(Type dbContextType, Guid tenantId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        using (_currentTenant.Change(tenantId))
        {
            return await TenantDatabaseDbContextActivator.ResolveConnectionAsync(
                dbContextType,
                scope.ServiceProvider.GetRequiredService<IConnectionStringResolver>(),
                _currentTenant);
        }
    }

    /// <summary>
    /// True when <paramref name="dbContextType"/> is a tenant-aware DbContext whose catalog name tenants may move to a
    /// dedicated database. Hosts without such a DbContext never need the migrator or its tenant-store dependencies.
    /// </summary>
    public static bool SupportsDedicatedDatabases(Type dbContextType) =>
        typeof(DbContext).IsAssignableFrom(dbContextType) &&
        !dbContextType.IsDefined(typeof(IgnoreMultiTenancyAttribute), inherit: false) &&
        TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(ConnectionStringNameAttribute.GetConnStringName(dbContextType));

    private IEnumerable<Type> GetDedicatableDbContextTypes() =>
        _options.Value.DbContextTypes.Distinct().Where(SupportsDedicatedDatabases);
}

/// <summary>Outcome of a startup pass over every dedicated tenant database.</summary>
/// <param name="Migrated">Dedicated databases migrated (and seeded) successfully.</param>
/// <param name="Failed">Dedicated databases that failed, conflict, or could not be resolved.</param>
/// <param name="TopologyIncomplete">The tenant list was unavailable, so dedicated databases may have been missed.</param>
public sealed record TenantDatabaseMigrationReport(int Migrated, int Failed, bool TopologyIncomplete);

/// <summary>
/// This service does not yet resolve the dedicated database the Tenant service committed (its tenant cache has not
/// converged). Transient: messaging retries it on the longer transient schedule.
/// </summary>
public sealed class TenantDatabaseAssignmentNotVisibleException
    : InvalidOperationException, ITenantWrightTransientMessageFailure
{
    public TenantDatabaseAssignmentNotVisibleException(string connectionStringName)
        : base($"The committed dedicated database assignment for '{connectionStringName}' is not visible yet.")
    {
    }
}

/// <summary>
/// Another replica or trigger holds the migration lock of this tenant database. Transient: once it finishes, the retry
/// finds nothing left to apply.
/// </summary>
public sealed class TenantDatabaseMigrationBusyException
    : InvalidOperationException, ITenantWrightTransientMessageFailure
{
    public TenantDatabaseMigrationBusyException(string connectionStringName)
        : base($"The dedicated database for '{connectionStringName}' is being migrated by another process.")
    {
    }
}

/// <summary>
/// The tenant's dedicated database is also used by another tenant. Not migrated or seeded; the tenant is excluded
/// from fan-out until an operator corrects the assignment.
/// </summary>
public sealed class TenantDatabaseConflictException : InvalidOperationException
{
    public TenantDatabaseConflictException(string connectionStringName)
        : base($"The dedicated database for '{connectionStringName}' is shared with another tenant; it is not migrated.")
    {
    }
}

/// <summary>
/// The tenant's dedicated database already holds schema this service did not create: migrations of another DbContext
/// (typically another service given the same physical database) or tables without any of this service's migrations.
/// Migrating it would collide with that schema and share one migration history, so it is refused before any DDL. An
/// operator must assign an empty database; the tenant fails closed until then.
/// </summary>
public sealed class TenantDatabaseForeignSchemaException : InvalidOperationException
{
    public TenantDatabaseForeignSchemaException(string connectionStringName, TenantDatabaseSchemaState schema)
        : base(
            $"The dedicated database for '{connectionStringName}' already holds schema this service did not create " +
            $"({schema.ForeignMigrationCount} foreign migrations, {schema.UnmanagedRelationCount} unmanaged relations); " +
            "assign an empty database.")
    {
        ConnectionStringName = connectionStringName;
        Schema = schema;
    }

    public string ConnectionStringName { get; }

    public TenantDatabaseSchemaState Schema { get; }
}
