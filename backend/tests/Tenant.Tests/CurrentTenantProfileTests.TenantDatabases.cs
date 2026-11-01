using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore.ConnectionStrings;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EventContracts;
using Tenant.Application.Contracts.Tenants;
using Tenant.Application.Tenants;
using Tenant.Domain.Shared;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;
using Xunit;

namespace Tenant.Tests;

/// <summary>
/// Named dedicated-database management: catalog-name validation, host-only enforcement, target uniqueness across
/// tenants and names (including the shared database), the no-data-migration acknowledgement, protection at rest, the
/// cache upsert and the <see cref="TenantDatabaseChangedEto"/> published with the change.
/// </summary>
public partial class CurrentTenantProfileTests
{
    private const string HostDatabase = "Host=shared-db;Database=tenant;Username=tenant_app;Password=host-secret";

    [Theory]
    [InlineData("Default")]
    [InlineData("Identity")]
    [InlineData("Tenant")]
    [InlineData("Auth")]
    [InlineData("notifications")]
    [InlineData("Unknown")]
    public async Task SetDatabaseAsync_rejects_names_outside_the_dedicatable_catalog(string name)
    {
        var fixture = await TenantDatabaseFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<SharedValidationException>(() =>
            fixture.Service.SetDatabaseAsync(fixture.TenantId, name, Input("Host=t1;Database=n1")));

        Assert.Equal(TenantErrorCodes.DedicatedDatabaseNameNotSupported, exception.Code);
        Assert.Empty(fixture.Messages.Published);
    }

    [Fact]
    public async Task SetDatabaseAsync_rejects_a_value_that_is_not_a_postgresql_target()
    {
        var fixture = await TenantDatabaseFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<SharedValidationException>(() =>
            fixture.Service.SetDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.Notifications, Input("not a connection string")));

        Assert.Equal(TenantErrorCodes.DedicatedDatabaseConnectionStringInvalid, exception.Code);
    }

    [Fact]
    public async Task SetDatabaseAsync_assigns_protects_caches_and_publishes_the_change()
    {
        var fixture = await TenantDatabaseFixture.CreateAsync();
        const string connectionString = "Host=tenant-db;Database=acme_notifications;Username=acme;Password=s3cret";

        var result = await fixture.Service.SetDatabaseAsync(
            fixture.TenantId,
            TenantWrightDatabaseCatalog.Notifications,
            Input(connectionString));

        Assert.Equal(TenantDatabaseAssignmentChange.Assigned, result.Change);
        Assert.False(result.DataMigrated);
        Assert.True(result.Status.IsDedicated);
        Assert.Equal(TenantWrightDatabaseCatalog.Notifications, result.Status.Name);
        Assert.False(string.IsNullOrWhiteSpace(result.Status.Fingerprint));

        var stored = await fixture.DbContext.TenantConnectionStrings.AsNoTracking().SingleAsync(x => x.TenantId == fixture.TenantId);
        Assert.Null(stored.Value);
        Assert.DoesNotContain("s3cret", stored.ProtectedValue!, StringComparison.Ordinal);
        Assert.Equal(PostgreSqlDatabaseTarget.FingerprintOf(connectionString), stored.TargetFingerprint);

        var cached = fixture.ByIdCache.Get(fixture.TenantId.ToString("D"));
        Assert.NotNull(cached);
        Assert.True(fixture.Protector.IsProtected(cached!.ConnectionStrings[TenantWrightDatabaseCatalog.Notifications]));

        var published = Assert.IsType<TenantDatabaseChangedEto>(Assert.Single(fixture.Messages.Published));
        Assert.Equal(fixture.TenantId, published.TenantId);
        Assert.Equal(TenantWrightDatabaseCatalog.Notifications, published.DatabaseName);
        Assert.Equal(TenantDatabaseChangeKindEto.Assigned, published.ChangeKind);
        Assert.Equal(stored.TargetFingerprint, published.TargetFingerprint);

        var list = await fixture.Service.GetDatabasesAsync(fixture.TenantId);
        Assert.Equal(TenantWrightDatabaseCatalog.TenantDedicatable, list.Items.Select(item => item.Name));
        Assert.Single(list.Items, item => item.IsDedicated);
    }

    [Fact]
    public async Task SetDatabaseAsync_requires_acknowledgement_for_first_and_changed_assignments()
    {
        var fixture = await TenantDatabaseFixture.CreateAsync();
        // A first assignment strands the tenant's shared rows just like a change, so it needs the acknowledgement too.
        var firstRefused = await Assert.ThrowsAsync<SharedValidationException>(() =>
            fixture.Service.SetDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.Permissions, Input("Host=t1;Database=perm_a", acknowledge: false)));
        Assert.Equal(TenantErrorCodes.DedicatedDatabaseDataMigrationNotAcknowledged, firstRefused.Code);
        Assert.Empty(fixture.Messages.Published);
        await fixture.Service.SetDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.Permissions, Input("Host=t1;Database=perm_a"));
        fixture.Messages.Published.Clear();

        var refused = await Assert.ThrowsAsync<SharedValidationException>(() =>
            fixture.Service.SetDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.Permissions, Input("Host=t1;Database=perm_b", acknowledge: false)));
        Assert.Equal(TenantErrorCodes.DedicatedDatabaseDataMigrationNotAcknowledged, refused.Code);
        Assert.Empty(fixture.Messages.Published);

        var reassigned = await fixture.Service.SetDatabaseAsync(
            fixture.TenantId,
            TenantWrightDatabaseCatalog.Permissions,
            Input("Host=t1;Database=perm_b", acknowledge: true));
        Assert.Equal(TenantDatabaseAssignmentChange.Reassigned, reassigned.Change);
        Assert.False(reassigned.DataMigrated);

        var rotated = await fixture.Service.SetDatabaseAsync(
            fixture.TenantId,
            TenantWrightDatabaseCatalog.Permissions,
            Input("Host=t1;Database=perm_b;Username=rotated;Password=new", acknowledge: true));
        Assert.Equal(TenantDatabaseAssignmentChange.Updated, rotated.Change);

        Assert.Equal(
            [TenantDatabaseChangeKindEto.Reassigned, TenantDatabaseChangeKindEto.Updated],
            fixture.Messages.Published.OfType<TenantDatabaseChangedEto>().Select(message => message.ChangeKind));
    }

    [Fact]
    public async Task SetDatabaseAsync_rejects_a_target_already_used_by_another_tenant_or_name()
    {
        var fixture = await TenantDatabaseFixture.CreateAsync();
        var otherTenantId = await fixture.AddTenantAsync("other-org");
        await fixture.Service.SetDatabaseAsync(otherTenantId, TenantWrightDatabaseCatalog.Notifications, Input("Host=tenant-db;Database=shared_by_mistake"));

        // Same physical database: host case and credentials differ, the normalized target does not.
        var sameTargetOtherTenant = await Assert.ThrowsAsync<SharedConflictException>(() =>
            fixture.Service.SetDatabaseAsync(
                fixture.TenantId,
                TenantWrightDatabaseCatalog.Notifications,
                Input("Host=TENANT-DB;Port=5432;Database=shared_by_mistake;Username=x;Password=y")));
        Assert.Equal(TenantErrorCodes.DedicatedDatabaseTargetInUse, sameTargetOtherTenant.Code);

        // Another name of the same tenant may not share a database either (service schemas would collide).
        await fixture.Service.SetDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.AuditLogs, Input("Host=tenant-db;Database=acme_audit"));
        var sameTargetOtherName = await Assert.ThrowsAsync<SharedConflictException>(() =>
            fixture.Service.SetDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.Permissions, Input("Host=tenant-db;Database=acme_audit")));
        Assert.Equal(TenantErrorCodes.DedicatedDatabaseTargetInUse, sameTargetOtherName.Code);
    }

    [Fact]
    public async Task SetDatabaseAsync_rejects_a_shared_database_known_to_the_host()
    {
        var fixture = await TenantDatabaseFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<SharedConflictException>(() =>
            fixture.Service.SetDatabaseAsync(
                fixture.TenantId,
                TenantWrightDatabaseCatalog.AuditLogs,
                Input("Host=shared-db;Database=tenant;Username=someone_else")));

        Assert.Equal(TenantErrorCodes.DedicatedDatabaseTargetInUse, exception.Code);
    }

    [Theory]
    [InlineData("Host=localhost;Database=acme_audit", "Host=127.0.0.1;Port=5432;Database=acme_audit")]
    [InlineData("Host=localhost;Database=acme_audit", "Host=::1;Database=acme_audit")]
    [InlineData("Host=localhost;Database=acme_audit", "Host=[0:0:0:0:0:0:0:1]:5432;Database=acme_audit")]
    [InlineData("Host=Tenant-DB.example.com;Database=acme_audit", "Host=tenant-db.example.com.;Port=5432;Database=ACME_Audit")]
    [InlineData("Host=tenant-db:5433;Database=acme_audit", "Host=TENANT-DB;Port=5433;Database=acme_audit")]
    public async Task SetDatabaseAsync_rejects_an_aliased_spelling_of_a_target_already_in_use(string first, string alias)
    {
        var fixture = await TenantDatabaseFixture.CreateAsync();
        await fixture.Service.SetDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.AuditLogs, Input(first));

        var exception = await Assert.ThrowsAsync<SharedConflictException>(() =>
            fixture.Service.SetDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.Notifications, Input(alias)));

        Assert.Equal(TenantErrorCodes.DedicatedDatabaseTargetInUse, exception.Code);
    }

    [Theory]
    [InlineData(TenantWrightDatabaseCatalog.AuditLogs, "Host=127.0.0.1;Port=5432;Database=tenantwright_auditlogs;Username=x")]
    [InlineData(TenantWrightDatabaseCatalog.Permissions, "Host=localhost;Database=TENANTWRIGHT_AUDITLOGS")]
    [InlineData(TenantWrightDatabaseCatalog.Notifications, "Host=pg.internal;Port=6432;Database=tenantwright_notifications")]
    public async Task SetDatabaseAsync_rejects_the_shared_database_of_any_dedicatable_service(string name, string connectionString)
    {
        var sharedTargets = new TenantSharedDatabaseTargetsOptions();
        sharedTargets.SharedTargets[TenantWrightDatabaseCatalog.AuditLogs] =
            new TenantSharedDatabaseTarget { Host = "localhost", Database = "tenantwright_auditlogs" };
        sharedTargets.SharedTargets[TenantWrightDatabaseCatalog.Notifications] =
            new TenantSharedDatabaseTarget { Host = "PG.internal.", Port = 6432, Database = "tenantwright_notifications" };
        var fixture = await TenantDatabaseFixture.CreateAsync(sharedTargets: sharedTargets);

        var exception = await Assert.ThrowsAsync<SharedConflictException>(() =>
            fixture.Service.SetDatabaseAsync(fixture.TenantId, name, Input(connectionString)));

        Assert.Equal(TenantErrorCodes.DedicatedDatabaseTargetInUse, exception.Code);
        Assert.Empty(fixture.Messages.Published);

        // A different database on the same server is still assignable.
        var assigned = await fixture.Service.SetDatabaseAsync(
            fixture.TenantId, name, Input("Host=localhost;Database=acme_dedicated"));
        Assert.Equal(TenantDatabaseAssignmentChange.Assigned, assigned.Change);
    }

    [Fact]
    public void Shared_database_targets_bind_targets_only_and_reject_credentials_and_unknown_names()
    {
        Assert.Equal(
            ["Host=pg", "Port=6432", "Database=tenantwright_auditlogs"],
            Bind(new Dictionary<string, string?>
            {
                ["TenantDatabases:SharedTargets:AuditLogs:Host"] = "pg",
                ["TenantDatabases:SharedTargets:AuditLogs:Port"] = "6432",
                ["TenantDatabases:SharedTargets:AuditLogs:Database"] = "tenantwright_auditlogs"
            }).SharedTargets.Select(pair => pair.Value).SelectMany(target =>
                new[] { $"Host={target.Host}", $"Port={target.Port}", $"Database={target.Database}" }));

        // A credential beside a target is a startup failure, never silently held.
        Assert.Throws<InvalidOperationException>(() => Bind(new Dictionary<string, string?>
        {
            ["TenantDatabases:SharedTargets:AuditLogs:Host"] = "pg",
            ["TenantDatabases:SharedTargets:AuditLogs:Database"] = "tenantwright_auditlogs",
            ["TenantDatabases:SharedTargets:AuditLogs:Password"] = "never"
        }));

        var unknownName = Assert.Throws<OptionsValidationException>(() => Bind(new Dictionary<string, string?>
        {
            ["TenantDatabases:SharedTargets:Identity:Host"] = "pg",
            ["TenantDatabases:SharedTargets:Identity:Database"] = "tenantwright_identity"
        }));
        Assert.Contains("Identity", unknownName.Message, StringComparison.Ordinal);

        Assert.Throws<OptionsValidationException>(() => Bind(new Dictionary<string, string?>
        {
            ["TenantDatabases:SharedTargets:Permissions:Database"] = "tenantwright_permissions"
        }));

        static TenantSharedDatabaseTargetsOptions Bind(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();
            var services = new ServiceCollection();
            services.AddTenantSharedDatabaseTargets(configuration);
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IOptions<TenantSharedDatabaseTargetsOptions>>().Value;
        }
    }

    [Fact]
    public async Task Fingerprint_refresh_recomputes_stale_fingerprints_and_clears_duplicates_it_uncovers()
    {
        var fixture = await TenantDatabaseFixture.CreateAsync();
        var otherTenantId = await fixture.AddTenantAsync("other-org");
        // Rows as an earlier normalization stored them: literal fingerprints that missed the loopback alias.
        AddStoredAssignment(fixture, fixture.TenantId, TenantWrightDatabaseCatalog.AuditLogs, "Host=localhost;Database=acme_audit", "stale-1");
        AddStoredAssignment(fixture, otherTenantId, TenantWrightDatabaseCatalog.AuditLogs, "Host=127.0.0.1;Database=acme_audit", "stale-2");
        AddStoredAssignment(fixture, otherTenantId, TenantWrightDatabaseCatalog.Permissions, "Host=tenant-db;Database=other_perm", "stale-3");
        await fixture.DbContext.SaveChangesOnDbContextAsync(true);
        var distributedLock = new RecordingDistributedLock();
        var refresh = new TenantConnectionTargetFingerprintRefreshService(
            new StaticDbContextProvider<TenantDbContextApp>(fixture.DbContext),
            fixture.Protector,
            distributedLock,
            NullLogger<TenantConnectionTargetFingerprintRefreshService>.Instance);

        Assert.Equal(3, await refresh.RefreshAsync());

        var rows = await fixture.DbContext.TenantConnectionStrings.AsNoTracking().ToListAsync();
        Assert.All(
            rows.Where(row => row.Name == TenantWrightDatabaseCatalog.AuditLogs),
            row => Assert.Null(row.TargetFingerprint));
        Assert.Equal(
            PostgreSqlDatabaseTarget.FingerprintOf("Host=tenant-db;Database=other_perm"),
            rows.Single(row => row.Name == TenantWrightDatabaseCatalog.Permissions).TargetFingerprint);
        Assert.Equal("tenant:connection-strings:target-fingerprints", Assert.Single(distributedLock.Acquisitions).Name);

        // Idempotent, and a cleared duplicate still blocks a third assignment through the decrypted comparison.
        Assert.Equal(0, await refresh.RefreshAsync());
        var thirdTenantId = await fixture.AddTenantAsync("third-org");
        var blocked = await Assert.ThrowsAsync<SharedConflictException>(() =>
            fixture.Service.SetDatabaseAsync(thirdTenantId, TenantWrightDatabaseCatalog.AuditLogs, Input("Host=::1;Database=ACME_AUDIT")));
        Assert.Equal(TenantErrorCodes.DedicatedDatabaseTargetInUse, blocked.Code);

        static void AddStoredAssignment(
            TenantDatabaseFixture fixture,
            Guid tenantId,
            string name,
            string connectionString,
            string storedFingerprint) =>
            fixture.DbContext.TenantConnectionStrings.Add(new global::Tenant.Domain.Tenants.TenantConnectionString(
                tenantId,
                name,
                fixture.Protector.Protect(tenantId, name, connectionString),
                "key-1",
                1,
                storedFingerprint));
    }

    [Fact]
    public async Task RemoveDatabaseAsync_requires_an_assignment_and_acknowledgement_then_publishes_removal()
    {
        var fixture = await TenantDatabaseFixture.CreateAsync();

        var notAssigned = await Assert.ThrowsAsync<SharedValidationException>(() =>
            fixture.Service.RemoveDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.AuditLogs, new RemoveTenantDatabaseInput { AcknowledgeNoDataMigration = true }));
        Assert.Equal(TenantErrorCodes.DedicatedDatabaseNotAssigned, notAssigned.Code);

        await fixture.Service.SetDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.AuditLogs, Input("Host=tenant-db;Database=acme_audit"));
        var unacknowledged = await Assert.ThrowsAsync<SharedValidationException>(() =>
            fixture.Service.RemoveDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.AuditLogs, new RemoveTenantDatabaseInput()));
        Assert.Equal(TenantErrorCodes.DedicatedDatabaseDataMigrationNotAcknowledged, unacknowledged.Code);

        var status = await fixture.Service.RemoveDatabaseAsync(
            fixture.TenantId,
            TenantWrightDatabaseCatalog.AuditLogs,
            new RemoveTenantDatabaseInput { AcknowledgeNoDataMigration = true });

        Assert.False(status.IsDedicated);
        Assert.False(await fixture.DbContext.TenantConnectionStrings.AnyAsync(x => x.TenantId == fixture.TenantId));
        var removed = fixture.Messages.Published.OfType<TenantDatabaseChangedEto>().Last();
        Assert.Equal(TenantDatabaseChangeKindEto.Removed, removed.ChangeKind);
        Assert.Null(removed.TargetFingerprint);
    }

    [Fact]
    public async Task SetDatabaseAsync_rejects_a_stale_concurrency_stamp()
    {
        var fixture = await TenantDatabaseFixture.CreateAsync();

        await Assert.ThrowsAsync<SharedConflictException>(() =>
            fixture.Service.SetDatabaseAsync(
                fixture.TenantId,
                TenantWrightDatabaseCatalog.Notifications,
                new SetTenantDatabaseInput { ConnectionString = "Host=t;Database=d", ConcurrencyStamp = "stale" }));
    }

    [Fact]
    public async Task Dedicated_database_management_is_host_only()
    {
        var fixture = await TenantDatabaseFixture.CreateAsync(tenantScoped: true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.GetDatabasesAsync(fixture.TenantId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Service.TestDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.Notifications));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Service.SetDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.Notifications, Input("Host=t;Database=d")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Service.RemoveDatabaseAsync(fixture.TenantId, TenantWrightDatabaseCatalog.Notifications, new RemoveTenantDatabaseInput { AcknowledgeNoDataMigration = true }));
        Assert.Empty(fixture.Messages.Published);
    }

    private static SetTenantDatabaseInput Input(string connectionString, bool acknowledge = true) =>
        new() { ConnectionString = connectionString, AcknowledgeNoDataMigration = acknowledge };

    private sealed class TenantDatabaseFixture
    {
        private TenantDatabaseFixture(
            Guid tenantId,
            TenantDbContextApp dbContext,
            TenantDatabaseAppService service,
            RecordingMessageSender messages,
            TenantConnectionStringProtector protector,
            InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantCacheItem, string> byIdCache)
        {
            TenantId = tenantId;
            DbContext = dbContext;
            Service = service;
            Messages = messages;
            Protector = protector;
            ByIdCache = byIdCache;
        }

        public Guid TenantId { get; }

        public TenantDbContextApp DbContext { get; }

        public TenantDatabaseAppService Service { get; }

        public RecordingMessageSender Messages { get; }

        public TenantConnectionStringProtector Protector { get; }

        public InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantCacheItem, string> ByIdCache { get; }

        public static async Task<TenantDatabaseFixture> CreateAsync(
            bool tenantScoped = false,
            TenantSharedDatabaseTargetsOptions? sharedTargets = null)
        {
            var tenantId = Guid.NewGuid();
            var currentTenant = new TestCurrentTenant(tenantScoped ? tenantId : null);
            var dbContext = CreateDbContext(new TestCurrentTenant(null));
            dbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(tenantId, "acme-org"));
            await dbContext.SaveChangesOnDbContextAsync(true);

            var protectionOptions = CreateProtectionOptions();
            var protector = new TenantConnectionStringProtector(
                new StaticOptionsMonitor<TenantConnectionStringProtectionOptions>(protectionOptions));
            var byIdCache = new InMemoryTypedCache<Shared.MultiTenancy.Caching.TenantCacheItem, string>();
            var hostOptions = new SharedDbConnectionOptions();
            hostOptions.ConnectionStrings.Default = HostDatabase;
            var messages = new RecordingMessageSender();

            var service = new TenantDatabaseAppService(
                CreateTenantRepository(dbContext),
                new StaticDbContextProvider<TenantDbContextApp>(dbContext),
                CreateTenantCacheManager(dbContext, protector, byIdCache),
                currentTenant,
                messages,
                protector,
                Options.Create(protectionOptions),
                new StaticOptionsMonitor<SharedDbConnectionOptions>(hostOptions),
                Options.Create(sharedTargets ?? new TenantSharedDatabaseTargetsOptions()),
                new NoopUnitOfWorkManager(),
                NullLogger<TenantDatabaseAppService>.Instance);

            return new TenantDatabaseFixture(tenantId, dbContext, service, messages, protector, byIdCache);
        }

        public async Task<Guid> AddTenantAsync(string name)
        {
            var id = Guid.NewGuid();
            DbContext.Tenants.Add(new global::Tenant.Domain.Tenants.Tenant(id, name));
            await DbContext.SaveChangesOnDbContextAsync(true);
            return id;
        }
    }
}
