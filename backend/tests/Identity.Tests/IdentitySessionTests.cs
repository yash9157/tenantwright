using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Identity.Application.Account;
using Identity.Application.Contracts.Users;
using Identity.Application.Contracts.Caching;
using Identity.Application.Users;
using Identity.Application.ApplicationUsers;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.Auditing;
using Shared.BackgroundTasks.Workers;
using Shared.Caching.Abstractions;
using Shared.Caching.Sessions;
using Shared.CurrentUser;
using Shared.Domain.Entities;
using Shared.EntityFrameworkCore;
using Shared.MultiTenancy;
using Shared.Permissions;
using Shared.Uow;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;
using Shared.Uow.Events;
using Xunit;

namespace Identity.Tests;

public class IdentitySessionTests
{
    [Fact]
    public async Task GetListAsync_EffectiveStatus_ExpiredWhenMarkerAbsent()
    {
        await using var dbContext = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("D");

        var user = CreateUser(userId, tenantId, "test.user", "test@example.com");
        var session = CreateSession(userId, tenantId, sessionId, IdentitySessionStatus.Active);
        dbContext.Users.Add(user);
        dbContext.IdentitySessions.Add(session);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var markerStore = new RecordingSessionMarkerStore();
        // Do not add to marker store (marker absent)

        var appService = CreateAppService(dbContext, userId, tenantId, markerStore);
        var list = await appService.GetListByUserIdAsync(userId, tenantId);

        var dto = Assert.Single(list);
        Assert.Equal(IdentitySessionStatus.Expired.ToString(), dto.Status);
    }

    [Fact]
    public async Task GetListAsync_EffectiveStatus_ExpiredWhenAbsoluteTimeout()
    {
        await using var dbContext = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("D");

        var user = CreateUser(userId, tenantId, "test.user", "test@example.com");
        // Created 8 days ago (limit is 7 days)
        var session = CreateSession(userId, tenantId, sessionId, IdentitySessionStatus.Active);
        session.CreationTime = DateTime.UtcNow.AddDays(-8);
        dbContext.Users.Add(user);
        dbContext.IdentitySessions.Add(session);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var markerStore = new RecordingSessionMarkerStore();
        await markerStore.SetActiveAsync(tenantId, userId, sessionId, "1", TimeSpan.FromDays(7));

        var appService = CreateAppService(dbContext, userId, tenantId, markerStore);
        var list = await appService.GetListByUserIdAsync(userId, tenantId);

        var dto = Assert.Single(list);
        Assert.Equal(IdentitySessionStatus.Expired.ToString(), dto.Status);
    }

    [Fact]
    public async Task IdentitySessionCleanupWorker_Should_MarkExpired_And_PurgeRetention()
    {
        await using var dbContext = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // 1. Active but past absolute expiry (created 8 days ago)
        var s1 = CreateSession(userId, tenantId, "s1", IdentitySessionStatus.Active);
        s1.CreationTime = DateTime.UtcNow.AddDays(-8);

        // 2. Active, idle for 40m (limit 30) but NOT past absolute (fresh CreationTime).
        // The worker must NOT expire this: idle is enforced by the Valkey marker TTL,
        // not the DB, because LastActiveTime is stale for marker-refreshed sessions.
        var s2 = CreateSession(userId, tenantId, "s2", IdentitySessionStatus.Active);
        s2.LastActiveTime = DateTime.UtcNow.AddMinutes(-40);

        // 3. Revoked and past retention (last active 35 days ago, limit is 30)
        var s3 = CreateSession(userId, tenantId, "s3", IdentitySessionStatus.Revoked);
        s3.LastActiveTime = DateTime.UtcNow.AddDays(-35);

        // 4. Revoked and within retention (last active 5 days ago)
        var s4 = CreateSession(userId, tenantId, "s4", IdentitySessionStatus.Revoked);
        s4.LastActiveTime = DateTime.UtcNow.AddDays(-5);

        dbContext.IdentitySessions.AddRange(s1, s2, s3, s4);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var options = Options.Create(new SessionValidationOptions
        {
            CleanupEnabled = true,
            CleanupIntervalSeconds = 60,
            RetentionDays = 30,
            MarkerTtlHours = 168,
            IdleTimeoutMinutes = 30
        });

        var worker = new IdentitySessionCleanupWorker(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.ImmediateUnitOfWorkExecutor(),
            IdentityTestSupport.GetDataFilter(dbContext),
            new TestOptionsMonitor(options.Value),
            NullLogger<IdentitySessionCleanupWorker>.Instance);

        var context = new BackgroundWorkerContext(null!, "identity-session-cleanup", 60000, DateTime.UtcNow, CancellationToken.None);
        await worker.ExecuteAsync(context);

        var dbSessions = await dbContext.IdentitySessions.IgnoreQueryFilters().AsNoTracking().ToListAsync();

        var dbS1 = dbSessions.SingleOrDefault(x => x.SessionId == "s1");
        var dbS2 = dbSessions.SingleOrDefault(x => x.SessionId == "s2");
        var dbS3 = dbSessions.SingleOrDefault(x => x.SessionId == "s3");
        var dbS4 = dbSessions.SingleOrDefault(x => x.SessionId == "s4");

        Assert.NotNull(dbS1);
        Assert.Equal(IdentitySessionStatus.Expired, dbS1.Status);

        // Idle-only (fresh CreationTime) must remain Active — the worker does not
        // decide idle expiry; the Valkey marker TTL does.
        Assert.NotNull(dbS2);
        Assert.Equal(IdentitySessionStatus.Active, dbS2.Status);

        // Past retention should be deleted
        Assert.Null(dbS3);

        // Within retention should remain
        Assert.NotNull(dbS4);
    }

    [Fact]
    public async Task ConcurrentSessionCap_Should_EvictOldest()
    {
        await using var dbContext = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var options = Options.Create(new SessionValidationOptions
        {
            MaxConcurrentSessions = 2,
            MarkerTtlHours = 168,
            IdleTimeoutMinutes = 30
        });

        var markerStore = new RecordingSessionMarkerStore();

        // Use real revocation service to test integrated correctness
        var notificationPublisher = new IdentityTestSupport.CapturingMessageSender();
        var revocationService = new IdentitySessionRevocationService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.InMemoryDistributedCache<IdentitySessionValidityCacheItem>(),
            markerStore,
            new NoopMessageSender(),
            new IdentityUserSecurityAlertPublisher(notificationPublisher, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityUserSecurityAlertPublisher>.Instance));

        var accountAppService = new AccountAppService(
            userManager: null!,
            signInManager: null!,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.InMemoryDistributedCache<IdentitySessionValidityCacheItem>(),
            new NoopIdentityAuthCacheInvalidator(),
            markerStore,
            options,
            revocationService,
            loginRiskEvaluator: null!,
            loginRiskOptions: null!,
            loginRiskAlertingOptions: null!,
            passwordResetOptions: null!,
            emailVerificationOptions: null!,
            interactiveLoginOptions: null!,
            loginAttemptProtectionOptions: null!,
            securityAlertPublisher: new IdentityUserSecurityAlertPublisher(notificationPublisher, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityUserSecurityAlertPublisher>.Instance),
            passwordResetQuotaCache: null!,
            loginAttemptQuotaCache: null!,
            interactiveLoginChallengeCache: null!,
            distributedLock: null!,
            roleManager: null!,
            currentTenant: new IdentityTestSupport.TestCurrentTenant(tenantId),
            eventBus: notificationPublisher);

        // Call private UpsertIdentitySessionAsync using reflection
        var method = typeof(AccountAppService).GetMethod(
            "UpsertIdentitySessionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("UpsertIdentitySessionAsync was not found.");

        var sess1Id = Guid.NewGuid().ToString("D");
        var sess2Id = Guid.NewGuid().ToString("D");
        var sess3Id = Guid.NewGuid().ToString("D");

        await InvokeUpsertAsync(sess1Id);
        await dbContext.SaveChangesOnDbContextAsync(true);

        await InvokeUpsertAsync(sess2Id);
        await dbContext.SaveChangesOnDbContextAsync(true);

        // Verify both are Active in DB
        var activeSessions = await dbContext.IdentitySessions.Where(x => x.UserId == userId).ToListAsync();
        Assert.Equal(2, activeSessions.Count(x => x.Status == IdentitySessionStatus.Active));

        await InvokeUpsertAsync(sess3Id);
        await dbContext.SaveChangesOnDbContextAsync(true);

        // Verify sess1Id was revoked
        var sess1 = await dbContext.IdentitySessions.SingleAsync(x => x.SessionId == sess1Id);
        Assert.Equal(IdentitySessionStatus.Revoked, sess1.Status);
        Assert.Equal("ConcurrentSessionCap", sess1.RevocationSource);

        // Verify sess2Id and sess3Id are Active
        var sess2 = await dbContext.IdentitySessions.SingleAsync(x => x.SessionId == sess2Id);
        Assert.Equal(IdentitySessionStatus.Active, sess2.Status);

        var sess3 = await dbContext.IdentitySessions.SingleAsync(x => x.SessionId == sess3Id);
        Assert.Equal(IdentitySessionStatus.Active, sess3.Status);

        // Verify marker was removed for sess1Id
        Assert.Contains(markerStore.RemovedMarkers, x => x.SessionId == sess1Id);

        async Task InvokeUpsertAsync(string sessionId)
        {
            var result = method.Invoke(
                accountAppService,
                [userId, tenantId, sessionId, "client-1", "127.0.0.1", "Chrome", "PC", "NY"])
                as Task<string?>;

            if (result == null)
            {
                throw new InvalidOperationException("UpsertIdentitySessionAsync returned an unexpected value.");
            }

            await result;
        }
    }

    [Fact]
    public async Task Managed_revoke_requires_a_reason_and_rejects_sessions_outside_the_caller_scope()
    {
        await using var dbContext = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var managedSession = CreateSession(userId, tenantId, "managed-session", IdentitySessionStatus.Active);
        dbContext.Users.Add(CreateUser(userId, tenantId, "managed.user", "managed@example.com"));
        dbContext.IdentitySessions.Add(managedSession);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var revocations = new RecordingSessionRevocationService();
        var appService = CreateAppService(
            dbContext,
            userId,
            tenantId,
            new RecordingSessionMarkerStore(),
            revocations);

        var result = await appService.RevokeManagedAsync(managedSession.Id, new ManagedSessionRevokeInput
        {
            Reason = "Support investigation"
        });

        Assert.Equal(1, result.RevokedCount);
        var call = Assert.Single(revocations.Calls);
        Assert.Equal([managedSession.Id], call.SessionIds);
        Assert.Equal("LogoutManagedSessionByAdmin", call.Action);
        Assert.Equal("Support investigation", call.Reason);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => appService.RevokeManagedAsync(
            Guid.NewGuid(),
            new ManagedSessionRevokeInput { Reason = "Out of scope" }));
    }

    [Fact]
    public async Task Revoke_all_by_user_uses_the_supplied_reason_or_a_legacy_safe_fallback()
    {
        await using var dbContext = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        dbContext.Users.Add(CreateUser(userId, tenantId, "managed.user", "managed@example.com"));
        await dbContext.SaveChangesOnDbContextAsync(true);

        var revocations = new RecordingSessionRevocationService();
        var appService = CreateAppService(dbContext, userId, tenantId, new RecordingSessionMarkerStore(), revocations);

        await appService.RevokeAllByUserIdAsync(userId, "Device reported lost");
        await appService.RevokeAllByUserIdAsync(userId);

        Assert.Collection(
            revocations.UserRevokeAllCalls,
            call => Assert.Equal("Device reported lost", call.Reason),
            call => Assert.Equal("Administrative session revoke", call.Reason));
    }

    [Fact]
    public async Task Revoke_all_by_tenant_records_the_operator_reason()
    {
        await using var dbContext = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        var revocations = new RecordingSessionRevocationService();
        var appService = CreateAppService(dbContext, operatorId, tenantId, new RecordingSessionMarkerStore(), revocations);

        await appService.RevokeAllByTenantIdAsync(tenantId, "Tenant security incident");

        var call = Assert.Single(revocations.TenantRevokeAllCalls);
        Assert.Equal(tenantId, call.TenantId);
        Assert.Equal("Tenant security incident", call.Reason);
    }

    [Fact]
    public async Task Host_admin_can_read_platform_staff_sessions_from_the_host_scope()
    {
        await using var dbContext = CreateDbContext();
        var hostUserId = Guid.NewGuid();
        var hostSession = CreateSession(hostUserId, Guid.Empty, "host-session", IdentitySessionStatus.Active);
        dbContext.Users.Add(CreateUser(hostUserId, Guid.Empty, "hostadmin", "hostadmin@example.test"));
        dbContext.IdentitySessions.Add(hostSession);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var appService = CreateAppService(dbContext, hostUserId, Guid.Empty, new RecordingSessionMarkerStore());

        var page = await appService.GetPagedListByUserIdAsync(hostUserId, new GetUserSessionsPagedInput());

        var session = Assert.Single(page.Items);
        Assert.Equal("host-session", session.SessionId);
    }

    [Fact]
    public async Task Host_admin_management_list_includes_platform_staff_sessions()
    {
        await using var dbContext = CreateDbContext();
        var hostUserId = Guid.NewGuid();
        var hostSession = CreateSession(hostUserId, Guid.Empty, "platform-staff-session", IdentitySessionStatus.Active);
        hostSession.TenantId = null;
        dbContext.Users.Add(CreateUser(hostUserId, Guid.Empty, "hostadmin", "hostadmin@example.test"));
        dbContext.IdentitySessions.Add(hostSession);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var currentUser = new IdentityTestSupport.TestCurrentUser(
            hostUserId,
            null,
            "hostadmin",
            TenantWrightIdentityRoleNames.HostAdmin);
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        var appService = new IdentitySessionAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            currentUser,
            new IdentityTestSupport.InMemoryDistributedCache<IdentitySessionValidityCacheItem>(),
            currentTenant,
            new RecordingSessionRevocationService(),
            new TenantUserManagementGuard(currentUser, currentTenant, userManager: null!),
            new StubTenantStore(),
            new RecordingSessionMarkerStore(),
            Options.Create(new SessionValidationOptions
            {
                MarkerTtlHours = 168,
                IdleTimeoutMinutes = 30
            }));

        var page = await appService.GetManagementPagedListAsync(new GetOperationalSessionsInput());

        var session = Assert.Single(page.Items);
        Assert.Equal("platform-staff-session", session.SessionId);
        Assert.Null(session.TenantId);
    }

    [Fact]
    public async Task ApplyUserSessionFilters_Should_Be_CaseInsensitive()
    {
        await using var dbContext = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("D");

        var user = CreateUser(userId, tenantId, "test.user", "test@example.com");
        var session = CreateSession(userId, tenantId, sessionId, IdentitySessionStatus.Active);
        session.UserAgent = "MySpecialBrowser";
        dbContext.Users.Add(user);
        dbContext.IdentitySessions.Add(session);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var appService = CreateAppService(dbContext, userId, tenantId, new RecordingSessionMarkerStore());

        // Search with lowercase filter
        var input = new GetUserSessionsPagedInput
        {
            Filter = "myspecialbrowser",
            Sorting = "LastActiveTime DESC"
        };
        var pagedList = await appService.GetPagedListByUserIdAsync(userId, input);

        var matched = Assert.Single(pagedList.Items);
        Assert.Equal(sessionId, matched.SessionId);
    }

    private static IdentityDbContextApp CreateDbContext()
    {
        return IdentityTestSupport.CreateDbContext();
    }

    private static ApplicationUser CreateUser(Guid userId, Guid tenantId, string userName, string email)
    {
        return new ApplicationUser
        {
            Id = userId,
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
    }

    private static IdentitySession CreateSession(Guid userId, Guid tenantId, string sessionId, IdentitySessionStatus status)
    {
        var now = DateTime.UtcNow;
        return new IdentitySession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            SessionId = sessionId,
            CreationTime = now.AddMinutes(-10),
            LastActiveTime = now.AddMinutes(-1),
            Status = status,
            ClientId = "test-client",
            IpAddress = "127.0.0.1",
            UserAgent = "TestBrowser"
        };
    }

    private static IdentitySessionAppService CreateAppService(
        IdentityDbContextApp dbContext,
        Guid userId,
        Guid tenantId,
        ISessionMarkerStore markerStore,
        IIdentitySessionRevocationService? sessionRevocationService = null)
    {
        var options = Options.Create(new SessionValidationOptions
        {
            MarkerTtlHours = 168,
            IdleTimeoutMinutes = 30
        });

        var currentUser = new IdentityTestSupport.TestCurrentUser(userId, tenantId, "test.user", TenantWrightIdentityRoleNames.HostAdmin);
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId);
        var guard = new TenantUserManagementGuard(currentUser, currentTenant, userManager: null!);

        return new IdentitySessionAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            currentUser,
            new IdentityTestSupport.InMemoryDistributedCache<IdentitySessionValidityCacheItem>(),
            currentTenant,
            sessionRevocationService: sessionRevocationService ?? new RecordingSessionRevocationService(),
            managementGuard: guard,
            tenantStore: new StubTenantStore(),
            sessionMarkerStore: markerStore,
            sessionValidationOptions: options);
    }

    private sealed class RecordingSessionMarkerStore : ISessionMarkerStore
    {
        public List<(Guid? TenantId, Guid UserId, string SessionId)> RemovedMarkers { get; } = [];
        private readonly Dictionary<string, string> _markers = new();

        public Task SetActiveAsync(Guid? tenantId, Guid userId, string sessionId, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            _markers[$"{tenantId}:{userId}:{sessionId}"] = value;
            return Task.CompletedTask;
        }

        public Task<string?> IsActiveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
        {
            _markers.TryGetValue($"{tenantId}:{userId}:{sessionId}", out var value);
            return Task.FromResult<string?>(value);
        }

        public Task RemoveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
        {
            RemovedMarkers.Add((tenantId, userId, sessionId));
            _markers.Remove($"{tenantId}:{userId}:{sessionId}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionRevocationService : IIdentitySessionRevocationService
    {
        public List<(IReadOnlyCollection<Guid> SessionIds, string Action, string? Reason)> Calls { get; } = [];
        public List<(Guid UserId, Guid? TenantId, string Action, string? Reason)> UserRevokeAllCalls { get; } = [];
        public List<(Guid TenantId, string Action, string? Reason)> TenantRevokeAllCalls { get; } = [];

        public Task<int> RevokeAllAsync(
            Guid userId,
            Guid? tenantId,
            string action,
            string? revocationSource = null,
            Guid? revokedByUserId = null,
            string? revocationReason = null,
            CancellationToken cancellationToken = default)
        {
            UserRevokeAllCalls.Add((userId, tenantId, action, revocationReason));
            return Task.FromResult(0);
        }

        public Task<int> RevokeAllByTenantAsync(
            Guid tenantId,
            string action,
            string? revocationSource = null,
            Guid? revokedByUserId = null,
            string? revocationReason = null,
            CancellationToken cancellationToken = default)
        {
            TenantRevokeAllCalls.Add((tenantId, action, revocationReason));
            return Task.FromResult(0);
        }

        public Task<BulkRevokeSessionsResultDto> RevokeSelectedAsync(
            IReadOnlyCollection<Guid> sessionIds,
            string action,
            string? revocationSource = null,
            Guid? revokedByUserId = null,
            string? revocationReason = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((sessionIds, action, revocationReason));
            return Task.FromResult(new BulkRevokeSessionsResultDto
            {
                RequestedCount = sessionIds.Count,
                RevokedCount = sessionIds.Count
            });
        }
    }

    private sealed class NoopUnitOfWorkManager : IUnitOfWorkManager
    {
        public IUnitOfWork? Current => null;
        public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => new NoopUnitOfWork();
        public IUnitOfWork Begin(bool requiresNew = false) => new NoopUnitOfWork();
        public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => new NoopUnitOfWork();
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) { }
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => true;
    }

    private sealed class NoopUnitOfWork : IUnitOfWork
    {
        public Guid Id { get; } = Guid.NewGuid();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
        public event EventHandler<UnitOfWorkFailedEventArgs> Failed
        {
            add { }
            remove { }
        }

        public event EventHandler<UnitOfWorkEventArgs> Disposed
        {
            add { }
            remove { }
        }

        public UnitOfWorkOptions Options { get; } = new();
        public IUnitOfWork? Outer => null;
        public bool IsReserved => false;
        public bool IsDisposed => false;
        public bool IsCompleted => false;
        public string? ReservationName => null;
        public IServiceProvider ServiceProvider => null!;

        public void SetOuter(IUnitOfWork? outer) { }
        public void Initialize(UnitOfWorkOptions options) { }
        public void Reserve(string reservationName) { }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void OnCompleted(Func<Task> handler) { }
        public TItem GetOrAddItem<TItem>(string key, Func<string, TItem> factory) => factory(key);


        public IDatabaseApi? FindDatabaseApi(string key) => null;
        public void AddDatabaseApi(string key, IDatabaseApi api) { }
        public IDatabaseApi GetOrAddDatabaseApi(string key, Func<IDatabaseApi> factory) => factory();

        public ITransactionApi? FindTransactionApi(string key) => null;
        public void AddTransactionApi(string key, ITransactionApi api) { }
        public ITransactionApi GetOrAddTransactionApi(string key, Func<ITransactionApi> factory) => factory();

        public void Dispose() { }
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<SessionValidationOptions>
    {
        public SessionValidationOptions CurrentValue { get; }

        public TestOptionsMonitor(SessionValidationOptions currentValue)
        {
            CurrentValue = currentValue;
        }

        public SessionValidationOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SessionValidationOptions, string?> listener) => null;
    }

    private sealed class NoopAuditPropertySetter : IAuditPropertySetter
    {
        public void SetCreationProperties(object target) { }
        public void SetModificationProperties(object target) { }
        public void SetDeletionProperties(object target) { }
        public void IncrementEntityVersionProperty(object target) { }
    }

    private sealed class NoopAuditingManager : IAuditingManager
    {
        public IAuditLogScope? Current => null;
        public IAuditLogSaveHandle BeginScope() => new NoopAuditLogScope();
    }

    private sealed class NoopAuditLogScope : IAuditLogScope, IAuditLogSaveHandle
    {
        public AuditLogInfo Log { get; } = new();
        public Task SaveAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class NoopMessageSender : Shared.EventBus.Messaging.ITenantWrightMessageSender
    {
        public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
    }

    private sealed class NoopIdentityAuthCacheInvalidator : IIdentityAuthCacheInvalidator
    {
        public Task InvalidateUserAuthCacheAsync(Guid userId, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task InvalidateProfileAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task InvalidateAuthorizationAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task InvalidateAliasesAsync(Guid? tenantId, IEnumerable<string> aliases, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubTenantStore : ITenantStore
    {
        public IReadOnlyList<TenantConfiguration> GetList() => [];
        public async Task<IReadOnlyList<TenantConfiguration>?> FindListAsync(CancellationToken cancellationToken = default) =>
            await GetListAsync(cancellationToken);

        public Task<IReadOnlyList<TenantConfiguration>> GetListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TenantConfiguration>>([]);
        public TenantConfiguration? Find(Guid id) => null;
        public TenantConfiguration? Find(string name) => null;
        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TenantConfiguration?>(null);
        public Task<TenantConfiguration?> FindAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<TenantConfiguration?>(null);
    }
}
