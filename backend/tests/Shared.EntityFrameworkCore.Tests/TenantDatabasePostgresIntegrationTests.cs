using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Realtime;
using Notifications.Application.Contracts.Templates;
using Notifications.Application.Delivery;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Npgsql;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.PermissionHistory;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.AuditLogs.Publishing;
using Shared.Data.ConnectionStrings;
using Shared.Data.Seeding;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.ConnectionStrings;
using Shared.EntityFrameworkCore.DependencyInjection;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EntityFrameworkCore.Migrations;
using Shared.EntityFrameworkCore.Repositories;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EntityFrameworkCore.Tests.TestDoubles;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

/// <summary>
/// Dedicated tenant databases against real PostgreSQL: a tenant's dedicated database is migrated under its own lock and
/// guarded until then, and every cross-tenant job reaches rows that live only in a dedicated database while never
/// processing rows a dedicated tenant left behind in the shared database.
/// </summary>
[Collection(NeutralMessagingPostgresSuite.Name)]
[Trait("Category", "PostgresIntegration")]
public sealed class TenantDatabasePostgresIntegrationTests(NeutralMessagingPostgresFixture postgres) : IDisposable
{
    private readonly MessagingTrustKeys _keys = new(NeutralMessagingTopology.Producer, NeutralMessagingTopology.Consumer);

    public void Dispose() => _keys.Dispose();

    [Fact]
    public async Task Migrator_migrates_a_dedicated_database_under_its_tenant_lock_and_the_guard_fails_closed_until_then()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<NotificationsDbContextApp>(shared);
        var seeder = new RecordingDataSeeder();
        await using var host = TenantDatabaseTestHost.Create(shared, services =>
        {
            AddServiceDbContext<NotificationsDbContextApp>(services, shared);
            services.AddSingleton<IDataSeeder>(seeder);
        });
        var tenantId = Guid.NewGuid();
        host.Tenants.Upsert(tenantId, dedicated: (TenantWrightDatabaseCatalog.Notifications, dedicated));

        // Unmigrated dedicated database: tenant work fails closed with a clear error instead of touching it.
        await Assert.ThrowsAsync<TenantDatabaseNotMigratedException>(() => CountNotificationsAsync(host, tenantId));

        var lockName = $"efcore:migrate:{typeof(NotificationsDbContextApp).FullName}:tenant:{tenantId:N}";
        var held = await host.Locks.HoldAsync(lockName);
        var migrator = host.Services.GetRequiredService<TenantDatabaseMigrator>();
        var migration = migrator.MigrateAllDedicatedAsync();

        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(migration.IsCompleted);
        Assert.Empty(await AppliedMigrationsAsync<NotificationsDbContextApp>(dedicated));

        await held.DisposeAsync();
        var report = await migration;

        Assert.Equal(new TenantDatabaseMigrationReport(Migrated: 1, Failed: 0, TopologyIncomplete: false), report);
        Assert.Equal(AllMigrations<NotificationsDbContextApp>(), await AppliedMigrationsAsync<NotificationsDbContextApp>(dedicated));
        Assert.Equal([tenantId], seeder.SeededTenants);
        Assert.Contains(lockName, host.Locks.AcquiredNames);

        // Concurrent replicas serialize on the same lock and find nothing left to apply.
        var reports = await Task.WhenAll(
            migrator.MigrateAllDedicatedAsync(),
            host.Services.GetRequiredService<TenantDatabaseMigrator>().MigrateAllDedicatedAsync());
        Assert.All(reports, again => Assert.Equal(0, again.Failed));
        Assert.Equal(AllMigrations<NotificationsDbContextApp>(), await AppliedMigrationsAsync<NotificationsDbContextApp>(dedicated));

        // Migrated: tenant work now reaches the dedicated database, host work the shared one.
        Assert.Equal(0, await CountNotificationsAsync(host, tenantId));
        Assert.Equal(DatabaseName(dedicated), await ConnectedDatabaseAsync(host, tenantId));
        Assert.Equal(DatabaseName(shared), await ConnectedDatabaseAsync(host, tenantId: null));
    }

    [Fact]
    public async Task Database_changed_event_migrates_and_seeds_the_tenant_and_refuses_a_cache_that_has_not_converged()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<PermissionsDbContextApp>(shared);
        var seeder = new RecordingDataSeeder();
        await using var host = TenantDatabaseTestHost.Create(shared, services =>
        {
            AddServiceDbContext<PermissionsDbContextApp>(services, shared);
            services.AddSingleton<IDataSeeder>(seeder);
            services.AddTransient<global::Permissions.Application.TenantDatabases.TenantDatabaseChangedEventHandler>();
        });
        var tenantId = Guid.NewGuid();
        var handler = host.Services.GetRequiredService<global::Permissions.Application.TenantDatabases.TenantDatabaseChangedEventHandler>();
        var changed = new TenantDatabaseChangedEto
        {
            TenantId = tenantId,
            DatabaseName = TenantWrightDatabaseCatalog.Permissions,
            ChangeKind = TenantDatabaseChangeKindEto.Assigned,
            TargetFingerprint = PostgreSqlDatabaseTarget.FingerprintOf(dedicated),
            OccurredAtUtc = DateTime.UtcNow
        };

        // The Tenant service committed the assignment but this service's tenant cache does not show it yet.
        host.Tenants.Upsert(tenantId);
        await Assert.ThrowsAsync<TenantDatabaseAssignmentNotVisibleException>(() => handler.HandleAsync(changed));
        Assert.Empty(await AppliedMigrationsAsync<PermissionsDbContextApp>(dedicated));

        host.Tenants.Upsert(tenantId, dedicated: (TenantWrightDatabaseCatalog.Permissions, dedicated));
        await handler.HandleAsync(changed);

        Assert.Equal(AllMigrations<PermissionsDbContextApp>(), await AppliedMigrationsAsync<PermissionsDbContextApp>(dedicated));
        Assert.Equal([tenantId], seeder.SeededTenants);

        // Events for a database another service owns are ignored.
        await handler.HandleAsync(new TenantDatabaseChangedEto
        {
            TenantId = tenantId,
            DatabaseName = TenantWrightDatabaseCatalog.AuditLogs,
            ChangeKind = TenantDatabaseChangeKindEto.Assigned,
            TargetFingerprint = "other",
            OccurredAtUtc = DateTime.UtcNow
        });
        Assert.Single(seeder.SeededTenants);
    }

    [Fact]
    public async Task Outbox_dispatcher_dispatches_a_message_written_in_a_dedicated_tenant_database()
    {
        var shared = await postgres.CreateDatabaseAsync();
        var dedicated = await postgres.CreateDatabaseAsync();
        var tenants = new InMemoryTenantStore();
        var tenantId = Guid.NewGuid();
        tenants.Upsert(tenantId, dedicated: (TenantWrightDatabaseCatalog.Notifications, dedicated));
        await using var producer = NeutralMessagingHost.Create(
            shared,
            _keys,
            NeutralMessagingTopology.Producer,
            consumes: false,
            configureServices: services => UseTenantResolution(services, shared, tenants));
        var key = Guid.NewGuid();

        await producer.InUnitOfWorkAsync(async (services, dbContext) =>
        {
            Assert.Equal(DatabaseName(dedicated), dbContext.Database.GetDbConnection().Database);
            dbContext.ProducerWrites.Add(new ProducerWrite(key));
            await NeutralMessagingHost.PublishAsync(services, tenantId, key);
            await dbContext.SaveChangesAsync();
        }, tenantId: tenantId);

        Assert.Empty(await producer.OutboxAsync());
        var written = Assert.Single(await OutboxAsync(dedicated));
        Assert.Equal(tenantId, written.TenantId);

        await producer.DrainAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, producer.Transport.Dispatches[written.Id]);
        Assert.NotNull(Assert.Single(await OutboxAsync(dedicated)).DispatchedAtUtc);
    }

    [Fact]
    public async Task Permission_cache_repair_rebuilds_providers_whose_grants_live_only_in_a_dedicated_database()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<PermissionsDbContextApp>(shared);
        await MigrateAsync<PermissionsDbContextApp>(dedicated);
        await using var host = TenantDatabaseTestHost.Create(shared, services =>
        {
            AddServiceDbContext<PermissionsDbContextApp>(services, shared);
            services.AddTransient<IRepository<PermissionGrant, Guid>, EfCoreRepository<PermissionsDbContextApp, PermissionGrant, Guid>>();
            AddPermissionCaches(services);
        });
        var dedicatedTenant = Guid.NewGuid();
        var sharedTenant = Guid.NewGuid();
        host.Tenants.Upsert(dedicatedTenant, dedicated: (TenantWrightDatabaseCatalog.Permissions, dedicated));
        host.Tenants.Upsert(sharedTenant);

        await AddGrantAsync(host, dedicatedTenant, Grant(dedicatedTenant, "dedicated-user"));
        await AddGrantAsync(host, sharedTenant, Grant(sharedTenant, "shared-user"));
        // Left behind in the shared database before the tenant moved: must never be served or repaired.
        await AddGrantAsync(host, scopeTenantId: null, Grant(dedicatedTenant, "orphaned-user"));

        await host.InUnitOfWorkAsync<bool>(null, async services =>
        {
            var repair = services.GetRequiredService<PermissionProviderCacheRepairService>();
            await services.GetRequiredService<TenantDatabaseFanOut>().ForEachDatabaseAsync<PermissionsDbContextApp>(
                "permissions-cache-refresh",
                (_, token) => repair.RefreshAllProvidersAsync(token));
            return true;
        }, transactional: false);

        var stamps = host.Services.GetRequiredService<PermissionStampService>();
        Assert.True(await stamps.GetAsync(dedicatedTenant, "U", "dedicated-user") > 0);
        Assert.True(await stamps.GetAsync(sharedTenant, "U", "shared-user") > 0);
        Assert.Equal(0, await stamps.GetAsync(dedicatedTenant, "U", "orphaned-user"));
    }

    [Fact]
    public async Task Notification_delivery_claims_from_a_dedicated_database_and_skips_rows_left_in_the_shared_one()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<NotificationsDbContextApp>(shared);
        await MigrateAsync<NotificationsDbContextApp>(dedicated);
        var handler = new CapturingEmailHandler();
        await using var host = TenantDatabaseTestHost.Create(shared, services =>
        {
            AddServiceDbContext<NotificationsDbContextApp>(services, shared);
            services.AddTransient<NotificationDeliveryWorker>();
            services.AddSingleton<IChannelDeliveryHandler>(handler);
            services.AddSingleton<INotificationsRealtimePublisher, NoopRealtimePublisher>();
            services.AddSingleton<IAuditLogWriteRequestPublisher, NoopAuditLogPublisher>();
            services.AddSingleton<INotificationsTemplateRenderer, FixedTemplateRenderer>();
            services.AddSingleton(Options.Create(new NotificationsDeliveryOptions
            {
                BatchSize = 10,
                MaxRetryCount = 3,
                ProcessingLeaseSeconds = 30,
                BaseRetryDelaySeconds = 1,
                MaxRetryDelaySeconds = 1
            }));
        });
        var dedicatedTenant = Guid.NewGuid();
        var sharedTenant = Guid.NewGuid();
        host.Tenants.Upsert(dedicatedTenant, dedicated: (TenantWrightDatabaseCatalog.Notifications, dedicated));
        host.Tenants.Upsert(sharedTenant);

        var dedicatedNotification = await QueueDeliveryAsync(host, dedicatedTenant, dedicatedTenant, deliveryId: 11);
        var sharedNotification = await QueueDeliveryAsync(host, sharedTenant, sharedTenant, deliveryId: 21);
        // Left behind in the shared database before the tenant moved: must never be claimed.
        var orphanedNotification = await QueueDeliveryAsync(host, scopeTenantId: null, dedicatedTenant, deliveryId: 22);

        var processed = await host.InUnitOfWorkAsync(null, async services =>
        {
            var worker = services.GetRequiredService<NotificationDeliveryWorker>();
            return await services.GetRequiredService<TenantDatabaseFanOut>().ForEachDatabaseAsync<NotificationsDbContextApp, int>(
                "notifications-delivery",
                (_, token) => worker.ProcessBatchAsync(token));
        }, transactional: false);

        Assert.Equal(2, processed.Sum());
        Assert.Equal(
            new[] { (sharedTenant, sharedNotification), (dedicatedTenant, dedicatedNotification) }.Order(),
            handler.Delivered.Order());
        Assert.NotEqual(NotificationStatus.Queued, await DeliveryStatusAsync(dedicated, 11));
        Assert.NotEqual(NotificationStatus.Queued, await DeliveryStatusAsync(shared, 21));
        Assert.Equal(NotificationStatus.Queued, await DeliveryStatusAsync(shared, 22));
        Assert.DoesNotContain(handler.Delivered, delivered => delivered.NotificationId == orphanedNotification);
    }

    [Fact]
    public async Task Platform_grant_history_merges_rows_from_dedicated_databases_into_one_ordered_page()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        await MigrateAsync<PermissionsDbContextApp>(shared);
        await MigrateAsync<PermissionsDbContextApp>(dedicated);
        await using var host = TenantDatabaseTestHost.Create(shared, services =>
        {
            AddServiceDbContext<PermissionsDbContextApp>(services, shared);
            services.AddTransient<IRepository<PermissionGrantHistoryEntry, Guid>, EfCoreRepository<PermissionsDbContextApp, PermissionGrantHistoryEntry, Guid>>();
            services.AddTransient<PermissionGrantHistoryAppService>();
        });
        var dedicatedTenant = Guid.NewGuid();
        var sharedTenant = Guid.NewGuid();
        host.Tenants.Upsert(dedicatedTenant, dedicated: (TenantWrightDatabaseCatalog.Permissions, dedicated));
        host.Tenants.Upsert(sharedTenant);
        var start = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        // Interleaved in time across the two databases, plus a row the dedicated tenant left in the shared database.
        await AddHistoryAsync(host, dedicatedTenant, History(dedicatedTenant, "dedicated-1", start.AddMinutes(4)));
        await AddHistoryAsync(host, sharedTenant, History(sharedTenant, "shared-1", start.AddMinutes(3)));
        await AddHistoryAsync(host, dedicatedTenant, History(dedicatedTenant, "dedicated-2", start.AddMinutes(2)));
        await AddHistoryAsync(host, sharedTenant, History(sharedTenant, "shared-2", start.AddMinutes(1)));
        await AddHistoryAsync(host, scopeTenantId: null, History(dedicatedTenant, "orphaned", start.AddMinutes(5)));

        var firstPage = await ReadPlatformHistoryAsync(host, skip: 0, take: 3);
        var secondPage = await ReadPlatformHistoryAsync(host, skip: 3, take: 3);

        Assert.Equal(4, firstPage.TotalCount);
        Assert.Equal(["dedicated-1", "shared-1", "dedicated-2"], firstPage.Items.Select(item => item.ProviderKey));
        Assert.Equal(["shared-2"], secondPage.Items.Select(item => item.ProviderKey));
        Assert.Equal(4, secondPage.TotalCount);

        // A cold tenant list cannot prove the view covers every tenant, so the read fails instead of omitting one.
        host.Tenants.ListAvailable = false;
        host.Services.GetRequiredService<ITenantDatabaseTopology>().Invalidate();
        await Assert.ThrowsAsync<TenantDatabaseTopologyUnavailableException>(() => ReadPlatformHistoryAsync(host, 0, 3));
    }

    [Fact]
    public async Task Topology_excludes_tenants_sharing_a_dedicated_database_and_reports_a_cold_tenant_list()
    {
        var shared = await postgres.CreateEmptyDatabaseAsync();
        var dedicated = await postgres.CreateEmptyDatabaseAsync();
        var alone = await postgres.CreateEmptyDatabaseAsync();
        await using var host = TenantDatabaseTestHost.Create(shared, services =>
            AddServiceDbContext<NotificationsDbContextApp>(services, shared));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        host.Tenants.Upsert(first, dedicated: (TenantWrightDatabaseCatalog.Notifications, dedicated));
        host.Tenants.Upsert(second, dedicated: (TenantWrightDatabaseCatalog.Notifications, dedicated.Replace("Pooling=True", "Pooling=False", StringComparison.Ordinal)));
        host.Tenants.Upsert(third, dedicated: (TenantWrightDatabaseCatalog.Notifications, alone));
        var topology = host.Services.GetRequiredService<ITenantDatabaseTopology>();

        var snapshot = await topology.GetSnapshotAsync(typeof(NotificationsDbContextApp));

        Assert.True(snapshot.IsComplete);
        Assert.Equal([null, third], snapshot.Databases.Select(database => database.TenantId));
        Assert.Equal(new HashSet<Guid> { first, second }, snapshot.ConflictingTenantIds);
        Assert.Equal(new HashSet<Guid> { first, second, third }, snapshot.TenantIdsWithDedicatedAssignment);

        host.Tenants.ListAvailable = false;
        topology.Invalidate();
        var cold = await topology.GetSnapshotAsync(typeof(NotificationsDbContextApp));
        Assert.False(cold.IsComplete);
        Assert.True(Assert.Single(cold.Databases).IsShared);
    }

    private static void UseTenantResolution(IServiceCollection services, string shared, InMemoryTenantStore tenants)
    {
        services.Configure<SharedDbConnectionOptions>(options => options.ConnectionStrings.Default = shared);
        services.Configure<SharedConnectionStringResolverOptions>(options => options.AllowHostFallbackForTenant = true);
        services.Replace(ServiceDescriptor.Transient<IConnectionStringResolver, TenantConnectionStringResolver>());
        services.Replace(ServiceDescriptor.Singleton<ITenantStore>(tenants));
        services.AddSingleton<ITenantConnectionStringProtector, PassThroughProtector>();
        services.AddSingleton<ITenantConnectionStringsCache, TenantConnectionStringsCache>();
        services.AddSingleton<TenantDatabaseMigrationGuard>();
    }

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

    private static void AddPermissionCaches(IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddDistributedMemoryCache();
        services.AddHybridCache();
        services.AddSingleton<Shared.Caching.Serialization.CacheSerializer>();
        services.AddSingleton<Shared.Caching.Abstractions.IDistributedCacheSerializer, Shared.Caching.Implementations.DefaultDistributedCacheSerializer>();
        services.AddSingleton<Shared.Caching.Abstractions.IDistributedCacheKeyNormalizer, Shared.Caching.Implementations.DefaultDistributedCacheKeyNormalizer>();
        services.AddSingleton<Shared.Caching.Abstractions.ICacheTenantResolver, Shared.Caching.Abstractions.NullCacheTenantResolver>();
        services.Configure<Shared.Caching.SharedDistributedCacheOptions>(_ => { });
        services.Configure<Shared.Caching.SharedHybridCacheOptions>(_ => { });
        services.AddSingleton<ITenantWrightMessageSender, NullTenantWrightMessageSender>();
        services.AddSingleton<PermissionProviderStampCache>();
        services.AddSingleton<PermissionProviderGrantsCache>();
        services.AddSingleton<RolePrivilegeMatrixCache>();
        services.AddTransient<PermissionStampService>();
        services.AddTransient<PermissionProviderGrantCacheSyncService>();
        services.AddTransient<PermissionProviderCacheRepairService>();
    }

    private static PermissionGrant Grant(Guid tenantId, string providerKey) =>
        new(Guid.NewGuid(), tenantId, "Permissions.Test", "U", providerKey, PermissionGrantEffect.Allow, DateTime.UtcNow);

    private static Task<bool> AddGrantAsync(TenantDatabaseTestHost host, Guid? scopeTenantId, PermissionGrant grant) =>
        host.InUnitOfWorkAsync(scopeTenantId, async services =>
        {
            var dbContext = await services.GetRequiredService<IDbContextProvider<PermissionsDbContextApp>>().GetDbContextAsync();
            dbContext.PermissionGrants.Add(grant);
            await dbContext.SaveChangesAsync();
            return true;
        });

    private static PermissionGrantHistoryEntry History(Guid tenantId, string providerKey, DateTime occurredAtUtc) =>
        new(
            Guid.NewGuid(),
            tenantId,
            "U",
            providerKey,
            "Permissions.Test",
            resourceName: null,
            resourceKey: null,
            resourceOwnerRevision: null,
            PermissionGrantHistoryChangeType.Granted,
            oldEffect: null,
            newEffect: PermissionGrantEffect.Allow,
            expiresAtUtc: null,
            reason: null,
            ticketId: null,
            actorUserId: null,
            occurredAtUtc);

    private static Task<bool> AddHistoryAsync(TenantDatabaseTestHost host, Guid? scopeTenantId, PermissionGrantHistoryEntry entry) =>
        host.InUnitOfWorkAsync(scopeTenantId, async services =>
        {
            var dbContext = await services.GetRequiredService<IDbContextProvider<PermissionsDbContextApp>>().GetDbContextAsync();
            dbContext.Add(entry);
            await dbContext.SaveChangesAsync();
            return true;
        });

    private static Task<PermissionGrantHistoryResultDto> ReadPlatformHistoryAsync(TenantDatabaseTestHost host, int skip, int take) =>
        host.InUnitOfWorkAsync(null, services => services.GetRequiredService<PermissionGrantHistoryAppService>()
            .GetPlatformListAsync(new GetPermissionGrantHistoryInput { SkipCount = skip, MaxResultCount = take }),
            transactional: false);

    private static Task<Guid> QueueDeliveryAsync(
        TenantDatabaseTestHost host,
        Guid? scopeTenantId,
        Guid tenantId,
        long deliveryId) =>
        host.InUnitOfWorkAsync(scopeTenantId, async services =>
        {
            var dbContext = await services.GetRequiredService<IDbContextProvider<NotificationsDbContextApp>>().GetDbContextAsync();
            var notification = new Notification(
                id: Guid.NewGuid(),
                tenantId: tenantId,
                requestId: Guid.NewGuid(),
                channel: NotificationChannel.Email,
                templateName: "Security.Alert",
                templateModelJson: "{}",
                subject: "Security alert");
            var recipient = new NotificationRecipient(
                id: deliveryId,
                tenantId: tenantId,
                notificationId: notification.Id,
                recipientType: NotificationRecipientType.EmailAddress,
                recipientValue: $"recipient-{deliveryId}@example.test");
            dbContext.Notifications.Add(notification);
            dbContext.NotificationRecipients.Add(recipient);
            dbContext.NotificationDeliveries.Add(new NotificationDelivery(deliveryId, tenantId, notification.Id, recipient.Id));
            await dbContext.SaveChangesAsync();
            return notification.Id;
        });

    private static async Task<NotificationStatus> DeliveryStatusAsync(string connectionString, long deliveryId)
    {
        await using var dbContext = Context<NotificationsDbContextApp>(connectionString);
        return await dbContext.NotificationDeliveries.IgnoreQueryFilters()
            .Where(delivery => delivery.Id == deliveryId)
            .Select(delivery => delivery.Status)
            .SingleAsync();
    }

    private static Task<int> CountNotificationsAsync(TenantDatabaseTestHost host, Guid tenantId) =>
        host.InUnitOfWorkAsync(tenantId, async services =>
        {
            var dbContext = await services.GetRequiredService<IDbContextProvider<NotificationsDbContextApp>>().GetDbContextAsync();
            return await dbContext.Notifications.CountAsync();
        });

    private static Task<string> ConnectedDatabaseAsync(TenantDatabaseTestHost host, Guid? tenantId) =>
        host.InUnitOfWorkAsync(tenantId, async services =>
        {
            var dbContext = await services.GetRequiredService<IDbContextProvider<NotificationsDbContextApp>>().GetDbContextAsync();
            return dbContext.Database.GetDbConnection().Database;
        });

    private static async Task MigrateAsync<TDbContext>(string connectionString)
        where TDbContext : DbContext
    {
        await using var dbContext = Context<TDbContext>(connectionString);
        await dbContext.Database.MigrateAsync();
    }

    private static async Task<List<string>> AppliedMigrationsAsync<TDbContext>(string connectionString)
        where TDbContext : DbContext
    {
        await using var dbContext = Context<TDbContext>(connectionString);
        return [.. await dbContext.Database.GetAppliedMigrationsAsync()];
    }

    private static List<string> AllMigrations<TDbContext>()
        where TDbContext : DbContext
    {
        using var dbContext = Context<TDbContext>("Host=unused;Database=unused");
        return [.. dbContext.Database.GetMigrations()];
    }

    private static async Task<List<MessagingOutboxMessage>> OutboxAsync(string connectionString)
    {
        await using var dbContext = Context<NeutralMessagingDbContext>(connectionString);
        return await dbContext.OutboxMessages.AsNoTracking().ToListAsync();
    }

    private static TDbContext Context<TDbContext>(string connectionString)
        where TDbContext : DbContext =>
        (TDbContext)Activator.CreateInstance(
            typeof(TDbContext),
            new DbContextOptionsBuilder<TDbContext>().UseNpgsql(connectionString).Options)!;

    private static string DatabaseName(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).Database!;

    private sealed class PassThroughProtector : ITenantConnectionStringProtector
    {
        public string Protect(Guid tenantId, string connectionName, string plaintext) => plaintext;
        public string Unprotect(Guid tenantId, string connectionName, string storedValue) => storedValue;
        public bool IsProtected(string? storedValue) => false;
    }

    private sealed class CapturingEmailHandler : IChannelDeliveryHandler
    {
        private readonly ConcurrentQueue<(Guid TenantId, Guid NotificationId)> _delivered = new();

        public NotificationChannel Channel => NotificationChannel.Email;

        public IReadOnlyCollection<(Guid TenantId, Guid NotificationId)> Delivered => [.. _delivered];

        public Task<ChannelDeliveryResult> DeliverAsync(ChannelDeliveryContext context, CancellationToken cancellationToken = default)
        {
            _delivered.Enqueue((context.TenantId!.Value, context.NotificationId));
            return Task.FromResult(ChannelDeliveryResult.Success($"provider-{context.NotificationId:N}"));
        }
    }

    private sealed class NoopRealtimePublisher : INotificationsRealtimePublisher
    {
        public Task PublishAppNotificationAsync(AppNotificationSignalDto input, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PublishSessionRevokedAsync(SessionRevokedSignalDto input, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopAuditLogPublisher : IAuditLogWriteRequestPublisher
    {
        public Task PublishAsync(AuditLogWriteRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FixedTemplateRenderer : INotificationsTemplateRenderer
    {
        public Task<RenderedNotificationTemplate> RenderAsync(
            string templateName,
            Dictionary<string, string?> model,
            CancellationToken cancellationToken = default) =>
            RenderAsync(templateName, model, applyLayout: true, cancellationToken);

        public Task<RenderedNotificationTemplate> RenderAsync(
            string templateName,
            Dictionary<string, string?> model,
            bool applyLayout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RenderedNotificationTemplate { Content = "<p>body</p>", Subject = "subject" });

        public Task<RenderedNotificationTemplate> RenderDraftAsync(
            string templateName,
            string bodyTemplate,
            string? subjectTemplate,
            Dictionary<string, string?> model,
            CancellationToken cancellationToken = default) =>
            RenderAsync(templateName, model, cancellationToken);
    }
}
