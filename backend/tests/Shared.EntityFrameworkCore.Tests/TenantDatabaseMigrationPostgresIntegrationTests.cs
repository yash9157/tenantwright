using System.Collections.Concurrent;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Npgsql;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Data.ConnectionStrings;
using Shared.Data.Seeding;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.EntityFrameworkCore.ConnectionStrings;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EntityFrameworkCore.Migrations;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EntityFrameworkCore.Tests.TestDoubles;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

/// <summary>
/// Dedicated tenant database migration through the path production uses — the service's
/// <c>TenantDatabaseChangedEventHandler</c> invoked inside the neutral inbox transaction — against real PostgreSQL and
/// the real AuditLogs, Notifications and Permissions DbContexts.
/// <para>
/// Regression for the live failure in which AuditLogs' migration of a dedicated database failed with
/// <c>42P07 relation "MessagingReplayState" already exists</c> on every attempt and the guard then reported every
/// AuditLogs migration pending: the database already held another service's fully migrated schema (every service's
/// Initial creates MessagingReplayState, and only AuditLogs' first eight tables are its own), so the history table held
/// that service's migration IDs, not AuditLogs'. EF itself applies a chain atomically, and the inbox transaction never
/// shares the migration's connection.
/// </para>
/// </summary>
[Collection(NeutralMessagingPostgresSuite.Name)]
[Trait("Category", "PostgresIntegration")]
public sealed class TenantDatabaseMigrationPostgresIntegrationTests(NeutralMessagingPostgresFixture postgres)
{
    private const string Endpoint = "tenantwright-auditlogs-tenant-database-changed-v1";

    [Fact]
    public async Task Change_event_converges_through_the_inbox_after_a_not_visible_attempt_while_tenant_work_fails_closed()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<AuditLogsDbContextApp>(shared);
        var seeder = new FlakySeeder();
        await using var host = CreateAuditLogsHost(shared, seeder);
        var tenantId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var changed = Changed(tenantId, TenantWrightDatabaseCatalog.AuditLogs, dedicated);

        // The Tenant service committed the assignment; this service's tenant cache does not show it yet.
        host.Tenants.Upsert(tenantId);
        var notVisible = await Assert.ThrowsAsync<TenantDatabaseAssignmentNotVisibleException>(
            () => DeliverAsync(host, messageId, changed));
        Assert.True(TenantWrightMessageFailureClassifier.IsTransient(notVisible));
        Assert.Empty(await InboxAsync(shared));
        Assert.Empty(await TablesAsync(dedicated));

        // The cache converged, but another replica holds the migration lock: tenant work meanwhile fails closed.
        host.Tenants.Upsert(tenantId, dedicated: (TenantWrightDatabaseCatalog.AuditLogs, dedicated));
        await host.Services.GetRequiredService<ITenantConnectionStringsCache>().RemoveAsync(tenantId);
        var held = await host.Locks.HoldAsync(LockName<AuditLogsDbContextApp>(tenantId));
        var retry = DeliverAsync(host, messageId, changed);
        var blocked = await TenantWorkOutcomesAsync(host, tenantId, attempts: 5);
        Assert.All(blocked, outcome => Assert.Equal(nameof(TenantDatabaseNotMigratedException), outcome));
        Assert.False(retry.IsCompleted);

        // Tenant work keeps running concurrently with the migration and only ever sees "blocked" or "migrated".
        using var stop = new CancellationTokenSource();
        var concurrent = Task.Run(() => TenantWorkUntilAsync(host, tenantId, stop.Token));
        await held.DisposeAsync();
        Assert.True(await retry);
        await stop.CancelAsync();
        Assert.All(
            await concurrent,
            outcome => Assert.Contains(outcome, new[] { "ok", nameof(TenantDatabaseNotMigratedException) }));

        Assert.Equal(AllMigrations<AuditLogsDbContextApp>(), await AppliedMigrationsAsync(dedicated));
        Assert.Contains("AuditArchiveManifests", await TablesAsync(dedicated));
        Assert.Equal([tenantId], seeder.Seeded);
        Assert.Equal([(messageId, Endpoint)], await InboxAsync(shared));
        Assert.Equal(["ok"], await TenantWorkOutcomesAsync(host, tenantId, attempts: 1));

        // A later event for the same assignment, and a redelivery of the first, change nothing.
        Assert.True(await DeliverAsync(host, Guid.NewGuid(), changed));
        Assert.False(await DeliverAsync(host, messageId, changed));
        Assert.Equal(AllMigrations<AuditLogsDbContextApp>(), await AppliedMigrationsAsync(dedicated));
    }

    [Fact]
    public async Task Migration_commits_on_its_own_connection_so_an_inbox_rollback_leaves_a_retryable_migrated_database()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<AuditLogsDbContextApp>(shared);
        var seeder = new FlakySeeder { FailuresRemaining = 1 };
        await using var host = CreateAuditLogsHost(shared, seeder);
        var tenantId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        host.Tenants.Upsert(tenantId, dedicated: (TenantWrightDatabaseCatalog.AuditLogs, dedicated));
        var changed = Changed(tenantId, TenantWrightDatabaseCatalog.AuditLogs, dedicated);

        // Seeding fails after the migration committed: the inbox transaction rolls back, the schema does not.
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverAsync(host, messageId, changed));
        Assert.Empty(await InboxAsync(shared));
        Assert.Equal(AllMigrations<AuditLogsDbContextApp>(), await AppliedMigrationsAsync(dedicated));

        // The redelivery finds nothing to apply (no 42P07) and seeds.
        Assert.True(await DeliverAsync(host, messageId, changed));
        Assert.Equal(AllMigrations<AuditLogsDbContextApp>(), await AppliedMigrationsAsync(dedicated));
        Assert.Equal([tenantId], seeder.Seeded);
    }

    [Fact]
    public async Task A_database_holding_another_services_schema_is_refused_before_any_ddl_and_fails_tenant_work_closed()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<AuditLogsDbContextApp>(shared);
        // The same physical database already serves this tenant's Notifications (assigned under a host alias, or reused).
        await MigrateAsync<NotificationsDbContextApp>(dedicated);
        var tablesBefore = await TablesAsync(dedicated);
        var historyBefore = await AppliedMigrationsAsync(dedicated);
        await using var host = CreateAuditLogsHost(shared, new FlakySeeder());
        var tenantId = Guid.NewGuid();
        host.Tenants.Upsert(tenantId, dedicated: (TenantWrightDatabaseCatalog.AuditLogs, dedicated));

        var refused = await Assert.ThrowsAsync<TenantDatabaseForeignSchemaException>(
            () => DeliverAsync(host, Guid.NewGuid(), Changed(tenantId, TenantWrightDatabaseCatalog.AuditLogs, dedicated)));

        Assert.False(TenantWrightMessageFailureClassifier.IsTransient(refused));
        Assert.Equal(AllMigrations<NotificationsDbContextApp>().Count, refused.Schema.ForeignMigrationCount);
        Assert.Equal(tablesBefore, await TablesAsync(dedicated));
        Assert.Equal(historyBefore, await AppliedMigrationsAsync(dedicated));
        Assert.Equal(
            [nameof(TenantDatabaseForeignSchemaException)],
            await TenantWorkOutcomesAsync(host, tenantId, attempts: 1));
    }

    [Fact]
    public async Task Tables_without_any_of_the_services_migrations_are_refused_for_every_dedicatable_context()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<PermissionsDbContextApp>(shared);
        await ExecuteAsync(dedicated, "CREATE TABLE public.\"Leftover\" (\"Id\" integer PRIMARY KEY)");
        await using var host = TenantDatabaseTestHost.Create(shared, services =>
        {
            AddServiceDbContext<PermissionsDbContextApp>(services, shared);
            services.AddTransient<global::Permissions.Application.TenantDatabases.TenantDatabaseChangedEventHandler>();
        });
        var tenantId = Guid.NewGuid();
        host.Tenants.Upsert(tenantId, dedicated: (TenantWrightDatabaseCatalog.Permissions, dedicated));
        var handler = host.Services.GetRequiredService<global::Permissions.Application.TenantDatabases.TenantDatabaseChangedEventHandler>();

        var refused = await Assert.ThrowsAsync<TenantDatabaseForeignSchemaException>(
            () => handler.HandleAsync(Changed(tenantId, TenantWrightDatabaseCatalog.Permissions, dedicated)));

        Assert.Equal(new TenantDatabaseSchemaState(AllMigrations<PermissionsDbContextApp>().Count, 0, 1), refused.Schema);
        Assert.Equal(["Leftover"], await TablesAsync(dedicated));
    }

    [Fact]
    public async Task Ef_applies_the_whole_chain_in_one_transaction_so_a_failed_attempt_leaves_no_partial_schema()
    {
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        // The ninth table of AuditLogs' Initial exists; the eight before it do not.
        await ExecuteAsync(
            dedicated,
            "CREATE TABLE public.\"MessagingReplayState\" (\"ReplayId\" text PRIMARY KEY)");

        await using (var dbContext = Context<AuditLogsDbContextApp>(dedicated))
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => dbContext.Database.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.DuplicateTable, failure.SqlState);
        }

        Assert.Empty(await AppliedMigrationsAsync(dedicated));
        Assert.DoesNotContain("AuditArchiveManifests", await TablesAsync(dedicated));
    }

    [Fact]
    public async Task Reconciler_migrates_a_dedicated_database_whose_change_event_was_lost()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<AuditLogsDbContextApp>(shared);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var seeder = new FlakySeeder();
        await using var host = CreateAuditLogsHost(shared, seeder, services => services.AddSingleton<TimeProvider>(clock));
        var tenantId = Guid.NewGuid();
        host.Tenants.Upsert(tenantId, dedicated: (TenantWrightDatabaseCatalog.AuditLogs, dedicated));
        var reconciler = host.Services.GetRequiredService<TenantDatabaseMigrationReconciler>();

        // No event ever arrives (dead-lettered). Tenant work fails closed and hands the database to the reconciler once.
        Assert.Equal(
            Enumerable.Repeat(nameof(TenantDatabaseNotMigratedException), 3),
            await TenantWorkOutcomesAsync(host, tenantId, attempts: 3));
        Assert.Equal(1, reconciler.PendingCount);

        // Nothing happens before the grace period that lets an in-flight event-driven migration finish.
        Assert.Equal(0, await reconciler.ReconcileDueAsync(CancellationToken.None));
        Assert.Empty(await AppliedMigrationsAsync(dedicated));

        clock.Advance(TenantDatabaseMigrationReconciler.InitialDelay);
        Assert.Equal(1, await reconciler.ReconcileDueAsync(CancellationToken.None));
        Assert.Equal(0, reconciler.PendingCount);
        Assert.Equal(AllMigrations<AuditLogsDbContextApp>(), await AppliedMigrationsAsync(dedicated));
        Assert.Equal([tenantId], seeder.Seeded);
        Assert.Contains(LockName<AuditLogsDbContextApp>(tenantId), host.Locks.AcquiredNames);
        Assert.Equal(["ok"], await TenantWorkOutcomesAsync(host, tenantId, attempts: 1));
    }

    [Fact]
    public async Task Reconciler_backs_off_a_database_it_cannot_migrate_and_keeps_the_tenant_closed()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<AuditLogsDbContextApp>(shared);
        await MigrateAsync<NotificationsDbContextApp>(dedicated);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var host = CreateAuditLogsHost(shared, new FlakySeeder(), services => services.AddSingleton<TimeProvider>(clock));
        var tenantId = Guid.NewGuid();
        host.Tenants.Upsert(tenantId, dedicated: (TenantWrightDatabaseCatalog.AuditLogs, dedicated));
        var reconciler = host.Services.GetRequiredService<TenantDatabaseMigrationReconciler>();
        await TenantWorkOutcomesAsync(host, tenantId, attempts: 1);

        clock.Advance(TenantDatabaseMigrationReconciler.InitialDelay);
        Assert.Equal(0, await reconciler.ReconcileDueAsync(CancellationToken.None));
        Assert.Equal(1, reconciler.PendingCount);

        // Backed off: not retried before the next slot, retried (and refused again) once it is due.
        clock.Advance(TenantDatabaseMigrationReconciler.BackOff(1) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await reconciler.ReconcileDueAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, await reconciler.ReconcileDueAsync(CancellationToken.None));
        Assert.Equal(
            2,
            host.Locks.AcquiredNames.Count(name => name == LockName<AuditLogsDbContextApp>(tenantId)));
        Assert.Equal(TimeSpan.FromMinutes(1), TenantDatabaseMigrationReconciler.BackOff(2));
        Assert.Equal(TenantDatabaseMigrationReconciler.MaximumDelay, TenantDatabaseMigrationReconciler.BackOff(50));
        Assert.Equal(
            [nameof(TenantDatabaseForeignSchemaException)],
            await TenantWorkOutcomesAsync(host, tenantId, attempts: 1));
    }

    [Fact]
    public async Task Lock_contention_surfaces_as_a_transient_failure()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<AuditLogsDbContextApp>(shared);
        await using var host = CreateAuditLogsHost(
            shared,
            new FlakySeeder(),
            services => services.AddSingleton<IDistributedLock, ContendedLock>());
        var tenantId = Guid.NewGuid();
        host.Tenants.Upsert(tenantId, dedicated: (TenantWrightDatabaseCatalog.AuditLogs, dedicated));

        var busy = await Assert.ThrowsAsync<TenantDatabaseMigrationBusyException>(
            () => DeliverAsync(host, Guid.NewGuid(), Changed(tenantId, TenantWrightDatabaseCatalog.AuditLogs, dedicated)));

        Assert.True(TenantWrightMessageFailureClassifier.IsTransient(busy));
        Assert.Empty(await TablesAsync(dedicated));
    }

    private static TenantDatabaseTestHost CreateAuditLogsHost(
        string shared,
        IDataSeeder seeder,
        Action<IServiceCollection>? configure = null) =>
        TenantDatabaseTestHost.Create(shared, services =>
        {
            AddServiceDbContext<AuditLogsDbContextApp>(services, shared);
            services.AddSingleton(seeder);
            services.AddSingleton(TimeProvider.System);
            services.AddScoped<ITenantWrightInboxTransaction, TenantWrightInboxTransaction<AuditLogsDbContextApp>>();
            services.AddTransient<
                ITenantWrightMessageHandler<TenantDatabaseChangedEto>,
                global::AuditLogs.Application.Events.TenantDatabaseChangedEventHandler>();
            configure?.Invoke(services);
        });

    private static void AddServiceDbContext<TDbContext>(IServiceCollection services, string shared)
        where TDbContext : DbContext
    {
        services.AddDbContext<TDbContext>(options =>
        {
            options.UseNpgsql(shared);
            options.AddSharedDbContextOptionsExtension();
        });
        services.AddSharedEfCoreAutoMigration<TDbContext>();
    }

    /// <summary>What <see cref="TenantWrightInboundMessagePipeline"/> does after trust verification: host scope, inbox, handler.</summary>
    private static async Task<bool> DeliverAsync(TenantDatabaseTestHost host, Guid messageId, TenantDatabaseChangedEto message)
    {
        using var hostScope = host.CurrentTenant.Change(null);
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITenantWrightInboxTransaction>().TryProcessOnceAsync(
            messageId,
            Endpoint,
            (services, token) => services.GetRequiredService<ITenantWrightMessageHandler<TenantDatabaseChangedEto>>()
                .HandleAsync(message, token),
            CancellationToken.None);
    }

    private static TenantDatabaseChangedEto Changed(Guid tenantId, string databaseName, string target) => new()
    {
        TenantId = tenantId,
        DatabaseName = databaseName,
        ChangeKind = TenantDatabaseChangeKindEto.Assigned,
        TargetFingerprint = PostgreSqlDatabaseTarget.FingerprintOf(target),
        OccurredAtUtc = DateTime.UtcNow
    };

    /// <summary>Tenant-scoped AuditLogs work, as the seal worker's fan-out runs it: "ok" or the failure type.</summary>
    private static async Task<List<string>> TenantWorkOutcomesAsync(TenantDatabaseTestHost host, Guid tenantId, int attempts)
    {
        var outcomes = new List<string>();
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            outcomes.Add(await TenantWorkOutcomeAsync(host, tenantId));
        }

        return outcomes;
    }

    private static async Task<List<string>> TenantWorkUntilAsync(TenantDatabaseTestHost host, Guid tenantId, CancellationToken stop)
    {
        var outcomes = new List<string>();
        while (!stop.IsCancellationRequested)
        {
            outcomes.Add(await TenantWorkOutcomeAsync(host, tenantId));
        }

        return outcomes;
    }

    private static async Task<string> TenantWorkOutcomeAsync(TenantDatabaseTestHost host, Guid tenantId)
    {
        try
        {
            await host.InUnitOfWorkAsync(tenantId, async services =>
            {
                var dbContext = await services.GetRequiredService<IDbContextProvider<AuditLogsDbContextApp>>().GetDbContextAsync();
                return await dbContext.AuditLogs.CountAsync();
            });
            return "ok";
        }
        catch (Exception exception)
        {
            return exception.GetType().Name;
        }
    }

    private static string LockName<TDbContext>(Guid tenantId) =>
        $"efcore:migrate:{typeof(TDbContext).FullName}:tenant:{tenantId:N}";

    private static async Task MigrateAsync<TDbContext>(string connectionString)
        where TDbContext : DbContext
    {
        await using var dbContext = Context<TDbContext>(connectionString);
        await dbContext.Database.MigrateAsync();
    }

    private static List<string> AllMigrations<TDbContext>()
        where TDbContext : DbContext
    {
        using var dbContext = Context<TDbContext>("Host=unused;Database=unused");
        return [.. dbContext.Database.GetMigrations()];
    }

    /// <summary>The history rows of any lineage, read without EF's probe.</summary>
    private static async Task<List<string>> AppliedMigrationsAsync(string connectionString)
    {
        if (!(await TablesAsync(connectionString)).Contains("__EFMigrationsHistory"))
        {
            return [];
        }

        return await QueryAsync(connectionString, "SELECT \"MigrationId\" FROM public.\"__EFMigrationsHistory\" ORDER BY 1");
    }

    private static Task<List<string>> TablesAsync(string connectionString) =>
        QueryAsync(connectionString, "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY 1");

    private static async Task<List<(Guid MessageId, string Endpoint)>> InboxAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT \"MessageId\", \"Endpoint\" FROM public.\"{TenantWrightMessagingTables.Inbox}\" ORDER BY \"ProcessedAtUtc\"",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(Guid, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return rows;
    }

    private static async Task<List<string>> QueryAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static TDbContext Context<TDbContext>(string connectionString)
        where TDbContext : DbContext =>
        (TDbContext)Activator.CreateInstance(
            typeof(TDbContext),
            new DbContextOptionsBuilder<TDbContext>().UseNpgsql(connectionString).Options)!;

    /// <summary>Records seeding passes per tenant and can fail the next ones on demand.</summary>
    private sealed class FlakySeeder : IDataSeeder
    {
        private readonly ConcurrentQueue<Guid> _seeded = new();
        private int _failuresRemaining;

        public int FailuresRemaining
        {
            get => Volatile.Read(ref _failuresRemaining);
            init => _failuresRemaining = value;
        }

        public IReadOnlyList<Guid> Seeded => [.. _seeded.Distinct()];

        public Task SeedAsync(DataSeedContext context)
        {
            if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
            {
                throw new InvalidOperationException("Seeding failed.");
            }

            _seeded.Enqueue(context.TenantId!.Value);
            return Task.CompletedTask;
        }
    }

    /// <summary>A lock another replica holds for longer than the acquire timeout.</summary>
    private sealed class ContendedLock : IDistributedLock
    {
        public Task<IDistributedLockHandle?> TryAcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IDistributedLockHandle?>(null);

        public Task<IDistributedLockHandle> AcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default) =>
            throw new TimeoutException($"Unable to acquire distributed lock '{name}'.");
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
