using System.Security.Claims;
using Identity.Application.Account;
using Identity.Application.Contracts.Users;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Shared.Caching.Abstractions;
using Shared.Caching.Sessions;
using Shared.CurrentUser;
using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Options;
using Xunit;

namespace Identity.Tests;

public class IdentitySessionRevocationServiceTests
{
    [Fact]
    public async Task RevokeSelectedAsync_should_preserve_rows_and_publish_per_revoked_session()
    {
        await using var dbContext = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var userA = CreateUser(tenantId, "tenant.user.a", "a@example.com");
        var userB = CreateUser(tenantId, "tenant.user.b", "b@example.com");

        var activeA = CreateSession(userA.Id, tenantId, "session-a", IdentitySessionStatus.Active);
        var activeB = CreateSession(userB.Id, tenantId, "session-b", IdentitySessionStatus.Active);
        var alreadyRevoked = CreateSession(userA.Id, tenantId, "session-c", IdentitySessionStatus.Revoked);

        dbContext.Users.AddRange(userA, userB);
        dbContext.IdentitySessions.AddRange(activeA, activeB, alreadyRevoked);
        await dbContext.SaveChangesOnDbContextAsync(true);

        var markerStore = new RecordingSessionMarkerStore();
        var eventBus = new RecordingMessageSender();
        var service = new IdentitySessionRevocationService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new InMemoryTypedCache<IdentitySessionValidityCacheItem>(),
            markerStore,
            eventBus,
            new IdentityUserSecurityAlertPublisher(new IdentityTestSupport.CapturingMessageSender(), Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityUserSecurityAlertPublisher>.Instance));

        var result = await service.RevokeSelectedAsync(
            [activeA.Id, activeB.Id, alreadyRevoked.Id],
            action: "LogoutSelectedSessionsByAdmin",
            revocationSource: "BulkRevoke",
            revokedByUserId: actorUserId,
            revocationReason: "SOC investigation");

        Assert.Equal(3, result.RequestedCount);
        Assert.Equal(2, result.RevokedCount);
        Assert.Equal(1, result.AlreadyRevokedCount);

        var sessions = await dbContext.IdentitySessions.IgnoreQueryFilters().OrderBy(x => x.SessionId).ToListAsync();
        Assert.All(sessions.Where(x => x.Id != alreadyRevoked.Id), session =>
        {
            Assert.Equal(IdentitySessionStatus.Revoked, session.Status);
            Assert.Equal(actorUserId, session.RevokedByUserId);
            Assert.Equal("BulkRevoke", session.RevocationSource);
            Assert.Equal("SOC investigation", session.RevocationReason);
            Assert.NotNull(session.RevokedAtUtc);
        });

        Assert.Equal(IdentitySessionStatus.Revoked, sessions.Single(x => x.Id == alreadyRevoked.Id).Status);
        Assert.Equal(2, markerStore.RemovedMarkers.Count);
        Assert.Equal(2, await dbContext.IdentitySecurityLogs.IgnoreQueryFilters().CountAsync());
        Assert.Equal(2, eventBus.Published.OfType<IdentitySessionRevokedTenantEto>().Count());
        Assert.Contains(
            eventBus.Published.OfType<IdentitySessionRevokedTenantEto>(),
            x => x.UserId == userA.Id && x.SessionId == activeA.SessionId);
        Assert.Contains(
            eventBus.Published.OfType<IdentitySessionRevokedTenantEto>(),
            x => x.UserId == userB.Id && x.SessionId == activeB.SessionId);
        Assert.All(eventBus.PublishRecords, record =>
        {
            Assert.Equal(typeof(IdentitySessionRevokedTenantEto), record.EventType);
            Assert.True(record.OnUnitOfWorkComplete);
            Assert.True(record.UseOutbox);
        });
    }

    [Fact]
    public async Task ValidateAsync_should_fail_closed_for_revoked_session_rows()
    {
        await using var dbContext = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("D");

        dbContext.IdentitySessions.Add(CreateSession(userId, tenantId, sessionId, IdentitySessionStatus.Revoked));
        await dbContext.SaveChangesOnDbContextAsync(true);

        var appService = new IdentitySessionAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new TestCurrentUser(userId, tenantId),
            new InMemoryTypedCache<IdentitySessionValidityCacheItem>(),
            new TestCurrentTenant(tenantId),
            sessionRevocationService: null!,
            managementGuard: null!,
            tenantStore: new StubTenantStore(),
            sessionMarkerStore: new RecordingSessionMarkerStore(),
            sessionValidationOptions: Microsoft.Extensions.Options.Options.Create(new Shared.Caching.Sessions.SessionValidationOptions()));

        var isValid = await appService.ValidateAsync(userId, sessionId, tenantId);

        Assert.False(isValid);
    }

    [Fact]
    public async Task GetListByUserIdAsync_should_scope_internal_list_to_explicit_tenant()
    {
        await using var dbContext = CreateDbContext();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userId = Guid.NewGuid();

        dbContext.IdentitySessions.AddRange(
            CreateSession(userId, tenantA, "tenant-a-session", IdentitySessionStatus.Active),
            CreateSession(userId, tenantB, "tenant-b-session", IdentitySessionStatus.Active));
        await dbContext.SaveChangesOnDbContextAsync(true);

        var appService = new IdentitySessionAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new TestCurrentUser(null, null),
            new InMemoryTypedCache<IdentitySessionValidityCacheItem>(),
            new TestCurrentTenant(null),
            sessionRevocationService: null!,
            managementGuard: null!,
            tenantStore: new StubTenantStore(),
            sessionMarkerStore: new RecordingSessionMarkerStore(),
            sessionValidationOptions: Microsoft.Extensions.Options.Options.Create(new Shared.Caching.Sessions.SessionValidationOptions()));

        var sessions = await appService.GetListByUserIdAsync(userId, tenantA);

        var session = Assert.Single(sessions);
        Assert.Equal("tenant-a-session", session.SessionId);
    }

    [Fact]
    public async Task RevokeBySessionIdAsync_should_scope_internal_revoke_to_explicit_tenant()
    {
        await using var dbContext = CreateDbContext();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var user = CreateUser(tenantA, "tenant.user", "tenant.user@example.com");

        dbContext.Users.Add(user);
        dbContext.IdentitySessions.AddRange(
            CreateSession(user.Id, tenantA, "shared-session", IdentitySessionStatus.Active),
            CreateSession(user.Id, tenantB, "shared-session", IdentitySessionStatus.Active));
        await dbContext.SaveChangesOnDbContextAsync(true);

        var markerStore = new RecordingSessionMarkerStore();
        var appService = new IdentitySessionAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new TestCurrentUser(null, null),
            new InMemoryTypedCache<IdentitySessionValidityCacheItem>(),
            new TestCurrentTenant(null),
            new IdentitySessionRevocationService(
                IdentityTestSupport.AsDbContextProvider(dbContext),
                new InMemoryTypedCache<IdentitySessionValidityCacheItem>(),
                markerStore,
                new RecordingMessageSender(),
                new IdentityUserSecurityAlertPublisher(new IdentityTestSupport.CapturingMessageSender(), Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityUserSecurityAlertPublisher>.Instance)),
            managementGuard: null!,
            tenantStore: new StubTenantStore(),
            sessionMarkerStore: markerStore,
            sessionValidationOptions: Microsoft.Extensions.Options.Options.Create(new Shared.Caching.Sessions.SessionValidationOptions()));

        await appService.RevokeBySessionIdAsync(user.Id, "shared-session", tenantA);

        var sessions = await dbContext.IdentitySessions.IgnoreQueryFilters().OrderBy(x => x.TenantId).ToListAsync();
        Assert.Equal(IdentitySessionStatus.Revoked, sessions.Single(x => x.TenantId == tenantA).Status);
        Assert.Equal(IdentitySessionStatus.Active, sessions.Single(x => x.TenantId == tenantB).Status);
        Assert.Single(markerStore.RemovedMarkers, marker => marker.TenantId == tenantA);
    }

    [Fact]
    public async Task RevokeBySessionIdAsync_should_treat_null_tenant_as_host_scope_only()
    {
        await using var dbContext = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var hostUser = CreateUser(Guid.Empty, "host.user", "host.user@example.com");

        dbContext.Users.Add(hostUser);
        dbContext.IdentitySessions.AddRange(
            CreateSession(hostUser.Id, null, "shared-session", IdentitySessionStatus.Active),
            CreateSession(hostUser.Id, tenantId, "shared-session", IdentitySessionStatus.Active));
        await dbContext.SaveChangesOnDbContextAsync(true);

        var markerStore = new RecordingSessionMarkerStore();
        var appService = new IdentitySessionAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new TestCurrentUser(Guid.NewGuid(), null),
            new InMemoryTypedCache<IdentitySessionValidityCacheItem>(),
            new TestCurrentTenant(null),
            new IdentitySessionRevocationService(
                IdentityTestSupport.AsDbContextProvider(dbContext),
                new InMemoryTypedCache<IdentitySessionValidityCacheItem>(),
                markerStore,
                new RecordingMessageSender(),
                new IdentityUserSecurityAlertPublisher(new IdentityTestSupport.CapturingMessageSender(), Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityUserSecurityAlertPublisher>.Instance)),
            managementGuard: null!,
            tenantStore: new StubTenantStore(),
            sessionMarkerStore: markerStore,
            sessionValidationOptions: Microsoft.Extensions.Options.Options.Create(new Shared.Caching.Sessions.SessionValidationOptions()));

        await appService.RevokeBySessionIdAsync(hostUser.Id, "shared-session", tenantId: null);

        var sessions = await dbContext.IdentitySessions.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(
            IdentitySessionStatus.Revoked,
            sessions.Single(x => x.TenantId == null).Status);
        Assert.Equal(
            IdentitySessionStatus.Active,
            sessions.Single(x => x.TenantId == tenantId).Status);
        Assert.Single(markerStore.RemovedMarkers, marker => marker.TenantId == null);
    }

    private static IdentityDbContextApp CreateDbContext()
    {
        return IdentityTestSupport.CreateDbContext();
    }

    private static ApplicationUser CreateUser(Guid tenantId, string userName, string email)
    {
        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
    }

    private static IdentitySession CreateSession(Guid userId, Guid? tenantId, string sessionId, IdentitySessionStatus status)
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
            RevokedAtUtc = status == IdentitySessionStatus.Revoked ? now.AddMinutes(-2) : null,
            RevocationSource = status == IdentitySessionStatus.Revoked ? "Seed" : null
        };
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        public TestCurrentUser(Guid? id, Guid? tenantId)
        {
            Id = id;
            TenantId = tenantId;
        }

        public bool IsAuthenticated => Id.HasValue;
        public Guid? Id { get; }
        public string? UserName => "tester";
        public string? Name => null;
        public string? SurName => null;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string? Email => null;
        public bool EmailVerified => false;
        public Guid? TenantId { get; }
        public string? SessionId => null;
        public string[] Roles => [];
        public Claim? FindClaim(string claimType) => null;
        public Claim[] FindClaims(string claimType) => [];
        public Claim[] GetAllClaims() => [];
        public bool IsInRole(string roleName) => false;
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name => null;

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previousId = Id;
            Id = id;
            return new DisposeAction(() => Id = previousId);
        }
    }

    private sealed class StubTenantStore : ITenantStore
    {
        public TenantConfiguration? Find(Guid id) => null;
        public TenantConfiguration? Find(string normalizedName) => null;
        public IReadOnlyList<TenantConfiguration> GetList() => [];
        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TenantConfiguration?>(null);
        public Task<TenantConfiguration?> FindAsync(string normalizedName, CancellationToken cancellationToken = default) => Task.FromResult<TenantConfiguration?>(null);
        public async Task<IReadOnlyList<TenantConfiguration>?> FindListAsync(CancellationToken cancellationToken = default) =>
            await GetListAsync(cancellationToken);

        public Task<IReadOnlyList<TenantConfiguration>> GetListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TenantConfiguration>>([]);
    }

    private sealed class DisposeAction : IDisposable
    {
        private readonly Action _dispose;

        public DisposeAction(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            _dispose();
        }
    }

    private sealed class RecordingSessionMarkerStore : ISessionMarkerStore
    {
        public List<(Guid? TenantId, Guid UserId, string SessionId)> RemovedMarkers { get; } = [];

        public Task SetActiveAsync(Guid? tenantId, Guid userId, string sessionId, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> IsActiveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("1");

        public Task RemoveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
        {
            RemovedMarkers.Add((tenantId, userId, sessionId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMessageSender : Shared.EventBus.Messaging.ITenantWrightMessageSender
    {
        public List<object> Published { get; } = [];
        public List<(Type EventType, object EventData, bool OnUnitOfWorkComplete, bool UseOutbox)> PublishRecords { get; } = [];

        public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class
        {
            Published.Add(message);
            PublishRecords.Add((typeof(T), message, true, true));
            return Task.CompletedTask;
        }
    }

    private sealed class NoopAuditPropertySetter : IAuditPropertySetter
    {
        public void SetCreationProperties(object targetObject) { }
        public void SetDeletionProperties(object targetObject) { }
        public void SetModificationProperties(object targetObject) { }
        public void IncrementEntityVersionProperty(object entity) { }
    }

    private sealed class NoopAuditingManager : IAuditingManager
    {
        public IAuditLogScope? Current => null;
        public IAuditLogSaveHandle BeginScope() => new NoopAuditLogScope();

        private sealed class NoopAuditLogScope : IAuditLogScope, IAuditLogSaveHandle
        {
            public AuditLogInfo Log { get; } = new();
            public Task SaveAsync() => Task.CompletedTask;
            public void Dispose() { }
        }
    }

    private sealed class NoopUnitOfWorkManager : Shared.Uow.Abstractions.IUnitOfWorkManager
    {
        public Shared.Uow.Abstractions.IUnitOfWork? Current => null;
        public Shared.Uow.Abstractions.IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Begin(bool requiresNew = false) => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => throw new NotSupportedException();
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) => throw new NotSupportedException();
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => false;
    }

    private sealed class InMemoryTypedCache<TCacheItem> : IDistributedCache<TCacheItem>
        where TCacheItem : class
    {
        private readonly Dictionary<string, TCacheItem> _items = new(StringComparer.Ordinal);

        public TCacheItem? Get(string key, bool? hideErrors = null, bool considerUow = false)
            => _items.GetValueOrDefault(key);

        public KeyValuePair<string, TCacheItem?>[] GetMany(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false)
            => keys.Select(x => new KeyValuePair<string, TCacheItem?>(x, _items.GetValueOrDefault(x))).ToArray();

        public Task<KeyValuePair<string, TCacheItem?>[]> GetManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
            => Task.FromResult(GetMany(keys, hideErrors, considerUow));

        public Task<TCacheItem?> GetAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
            => Task.FromResult(Get(key, hideErrors, considerUow));

        public TCacheItem? GetOrAdd(string key, Func<TCacheItem> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
            => _items.TryGetValue(key, out var existing) ? existing : (_items[key] = factory());

        public Task<TCacheItem?> GetOrAddAsync(string key, Func<Task<TCacheItem>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
            => _items.TryGetValue(key, out var existing)
                ? Task.FromResult<TCacheItem?>(existing)
                : SetFromFactoryAsync(key, factory);

        public KeyValuePair<string, TCacheItem?>[] GetOrAddMany(IEnumerable<string> keys, Func<IEnumerable<string>, List<KeyValuePair<string, TCacheItem>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
        {
            var missing = keys.Where(x => !_items.ContainsKey(x)).ToList();
            foreach (var pair in factory(missing))
            {
                _items[pair.Key] = pair.Value;
            }

            return GetMany(keys, hideErrors, considerUow);
        }

        public async Task<KeyValuePair<string, TCacheItem?>[]> GetOrAddManyAsync(IEnumerable<string> keys, Func<IEnumerable<string>, Task<List<KeyValuePair<string, TCacheItem>>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            var missing = keys.Where(x => !_items.ContainsKey(x)).ToList();
            foreach (var pair in await factory(missing))
            {
                _items[pair.Key] = pair.Value;
            }

            return GetMany(keys, hideErrors, considerUow);
        }

        public void Set(string key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
            => _items[key] = value;

        public Task SetAsync(string key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            _items[key] = value;
            return Task.CompletedTask;
        }

        public void SetMany(IEnumerable<KeyValuePair<string, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
        {
            foreach (var pair in items)
            {
                _items[pair.Key] = pair.Value;
            }
        }

        public Task SetManyAsync(IEnumerable<KeyValuePair<string, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            SetMany(items, options, hideErrors, considerUow);
            return Task.CompletedTask;
        }

        public void Refresh(string key, bool? hideErrors = null) { }
        public Task RefreshAsync(string key, bool? hideErrors = null, CancellationToken token = default) => Task.CompletedTask;
        public void RefreshMany(IEnumerable<string> keys, bool? hideErrors = null) { }
        public Task RefreshManyAsync(IEnumerable<string> keys, bool? hideErrors = null, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key, bool? hideErrors = null, bool considerUow = false) => _items.Remove(key);
        public Task RemoveAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            _items.Remove(key);
            return Task.CompletedTask;
        }

        public void RemoveMany(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false)
        {
            foreach (var key in keys)
            {
                _items.Remove(key);
            }
        }

        public Task RemoveManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            RemoveMany(keys, hideErrors, considerUow);
            return Task.CompletedTask;
        }

        private async Task<TCacheItem?> SetFromFactoryAsync(string key, Func<Task<TCacheItem>> factory)
        {
            var created = await factory();
            _items[key] = created;
            return created;
        }
    }
}
