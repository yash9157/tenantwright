using System.Security.Claims;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Dashboard;
using Identity.Application.Dashboard;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.Caching.Abstractions;
using Shared.EntityFrameworkCore;
using Shared.MultiTenancy;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Options;
using Xunit;

namespace Identity.Tests;

public class IdentityDashboardAppServiceTests
{
    [Fact]
    public async Task GetAdminAsync_should_return_cross_tenant_summary_and_widgets()
    {
        var now = DateTime.UtcNow;
        await using var dbContext = CreateDbContext();
        SeedDashboardData(dbContext, now, out var tenantA, out var _, out var userA1, out var pendingA);

        var service = new IdentityDashboardAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(null),
            IdentityTestSupport.GetDataFilter(dbContext),
            new InMemoryHybridCache<AdminIdentityDashboardDto>(),
            new InMemoryHybridCache<MfaAdoptionDto>(),
            new InMemoryHybridCache<TenantIdentityDashboardDto>());

        var dashboard = await service.GetAdminAsync(30);

        Assert.Equal(30, dashboard.WindowDays);
        Assert.InRange(dashboard.GeneratedAtUtc, now.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(2, dashboard.TotalTenantAdmins);
        Assert.Equal(5, dashboard.TotalTenantUsers);
        Assert.Equal(1, dashboard.InactiveUsers);
        Assert.Equal(1, dashboard.LockedUsers);
        Assert.Equal(1, dashboard.PendingActivationUsers);
        Assert.Equal(3, dashboard.ActiveSessions);
        Assert.Equal(2, dashboard.RevokedSessionsLast24h);
        Assert.Equal(3, dashboard.PendingInvitations);
        Assert.Equal(3, dashboard.ExpiringInvitationsNext72h);
        Assert.Equal(2, dashboard.LoginFailuresLast24h);
        Assert.Equal(1, dashboard.LoginLockedOutLast24h);
        Assert.Equal(2, dashboard.RiskBlockedLast24h);
        Assert.Equal(1, dashboard.TwoFactorChallengesLast24h);
        Assert.Equal(30, dashboard.LoginOutcomeTrend.Count);
        Assert.Equal(30, dashboard.SessionRevocationTrend.Count);
        Assert.Equal(30, dashboard.InvitationLifecycleTrend.Count);
        Assert.Equal(3, dashboard.ExpiringInvitations.Count);
        Assert.All(dashboard.ExpiringInvitations, x => Assert.True(x.ExpiresAtUtc > now));
        Assert.DoesNotContain(dashboard.ExpiringInvitations, x => x.Email == "accepted-a@example.com");
        Assert.Contains(dashboard.RecentSecurityActions, x => x.Action == "ManualLock" && x.TenantId == tenantA);
        Assert.Contains(dashboard.RecentSecurityActions, x => x.Action == "ManualUnlock");
        Assert.Equal(3, dashboard.AtRiskUsers.Count);
        Assert.Equal(userA1.Id, dashboard.AtRiskUsers[0].UserId);
        Assert.Equal(2, dashboard.AtRiskUsers[0].FailedLoginCount);
        Assert.Contains(dashboard.AtRiskUsers, x => x.UserId == pendingA.Id && x.RiskBlockedCount == 1);

        Assert.Equal(2, dashboard.LoginOutcomeTrend.Sum(x => x.LoginSuccessCount));
        Assert.Equal(1, dashboard.LoginOutcomeTrend.Sum(x => x.LoginFailedCount));
        Assert.Equal(1, dashboard.LoginOutcomeTrend.Sum(x => x.LoginInvalidPasswordCount));
        Assert.Equal(1, dashboard.LoginOutcomeTrend.Sum(x => x.LoginLockedOutCount));
        Assert.Equal(2, dashboard.LoginOutcomeTrend.Sum(x => x.LoginRiskBlockedCount));
        Assert.Equal(1, dashboard.LoginOutcomeTrend.Sum(x => x.TwoFactorChallengeCount));

        Assert.Equal(1, dashboard.InvitationLifecycleTrend.Sum(x => x.CanceledCount));
        Assert.Equal(1, dashboard.InvitationLifecycleTrend.Sum(x => x.ExpiredCount));

    }

    [Fact]
    public async Task GetTenantAsync_should_be_tenant_scoped_and_rank_at_risk_users()
    {
        var now = DateTime.UtcNow;
        await using var dbContext = CreateDbContext();
        SeedDashboardData(dbContext, now, out var tenantA, out var _, out var userA1, out var pendingA);

        var service = new IdentityDashboardAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(tenantA),
            IdentityTestSupport.GetDataFilter(dbContext),
            new InMemoryHybridCache<AdminIdentityDashboardDto>(),
            new InMemoryHybridCache<MfaAdoptionDto>(),
            new InMemoryHybridCache<TenantIdentityDashboardDto>());

        var dashboard = await service.GetTenantAsync(30);

        Assert.Equal(tenantA, dashboard.TenantId);
        Assert.InRange(dashboard.GeneratedAtUtc, now.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(1, dashboard.TenantAdminCount);
        Assert.Equal(4, dashboard.TenantUserCount);
        Assert.Equal(1, dashboard.InactiveUsers);
        Assert.Equal(1, dashboard.LockedUsers);
        Assert.Equal(1, dashboard.PendingActivationUsers);
        Assert.Equal(2, dashboard.ActiveSessions);
        Assert.Equal(1, dashboard.RevokedSessionsLast24h);
        Assert.Equal(2, dashboard.PendingInvitations);
        Assert.Equal(2, dashboard.ExpiringInvitationsNext72h);
        Assert.Equal(2, dashboard.LoginFailuresLast24h);
        Assert.Equal(1, dashboard.LoginLockedOutLast24h);
        Assert.Equal(1, dashboard.RiskBlockedLast24h);
        Assert.Equal(1, dashboard.TwoFactorChallengesLast24h);
        Assert.Equal(2, dashboard.ExpiringInvitations.Count);
        Assert.All(dashboard.RecentSecurityActions, x => Assert.Equal(tenantA, x.TenantId));
        Assert.Equal(2, dashboard.AtRiskUsers.Count);
        Assert.Equal(userA1.Id, dashboard.AtRiskUsers[0].UserId);
        Assert.Equal(2, dashboard.AtRiskUsers[0].FailedLoginCount);
        Assert.Equal(pendingA.Id, dashboard.AtRiskUsers[1].UserId);
        Assert.Equal(1, dashboard.AtRiskUsers[1].RiskBlockedCount);
    }

    [Fact]
    public async Task GetMfaAdoptionAsync_should_rank_weakest_tenants_and_skip_tiny_tenants()
    {
        await using var dbContext = CreateDbContext();
        var strongTenant = Guid.NewGuid();
        var weakTenant = Guid.NewGuid();
        var tinyTenant = Guid.NewGuid();

        // Strong tenant: 3 of 4 enrolled (75%). Weak tenant: 1 of 4 (25%). Tiny: below floor.
        var users = new List<ApplicationUser>();
        void Add(Guid tenant, int total, int enabled)
        {
            for (var i = 0; i < total; i++)
            {
                var user = CreateUser(tenant, $"u-{tenant:N}-{i}", $"u-{tenant:N}-{i}@example.com");
                user.TwoFactorEnabled = i < enabled;
                users.Add(user);
            }
        }
        Add(strongTenant, 4, 3);
        Add(weakTenant, 4, 1);
        Add(tinyTenant, 2, 0);
        dbContext.Users.AddRange(users);
        dbContext.SaveChanges();

        var service = new IdentityDashboardAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(null),
            IdentityTestSupport.GetDataFilter(dbContext),
            new InMemoryHybridCache<AdminIdentityDashboardDto>(),
            new InMemoryHybridCache<MfaAdoptionDto>(),
            new InMemoryHybridCache<TenantIdentityDashboardDto>());

        var result = await service.GetMfaAdoptionAsync(10);

        // Tiny tenant (2 users) is excluded from the worklist but still counts platform-wide.
        Assert.Equal(2, result.Tenants.Count);
        Assert.Equal(weakTenant, result.Tenants[0].TenantId); // weakest first
        Assert.Equal(0.25m, result.Tenants[0].AdoptionRate);
        Assert.Equal(strongTenant, result.Tenants[1].TenantId);
        Assert.Equal(0.75m, result.Tenants[1].AdoptionRate);
        Assert.Equal(10, result.PlatformTotalCount);
        Assert.Equal(4, result.PlatformEnabledCount);
        Assert.Equal(0.4m, result.PlatformAdoptionRate);
    }

    [Fact]
    public async Task GetAdminAsync_should_fail_closed_for_invalid_window_days()
    {
        await using var dbContext = CreateDbContext();
        var service = new IdentityDashboardAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(null),
            IdentityTestSupport.GetDataFilter(dbContext),
            new InMemoryHybridCache<AdminIdentityDashboardDto>(),
            new InMemoryHybridCache<MfaAdoptionDto>(),
            new InMemoryHybridCache<TenantIdentityDashboardDto>());

        await Assert.ThrowsAsync<SharedValidationException>(() => service.GetAdminAsync(14));
    }

    private static void SeedDashboardData(
        IdentityDbContextApp dbContext,
        DateTime now,
        out Guid tenantA,
        out Guid tenantB,
        out ApplicationUser userA1,
        out ApplicationUser pendingA)
    {
        tenantA = Guid.NewGuid();
        tenantB = Guid.NewGuid();
        var hostRole = CreateRole(TenantWrightIdentityRoleNames.HostAdmin, tenantId: null);
        var tenantAdminRoleA = CreateRole(TenantWrightIdentityRoleNames.TenantAdmin, tenantA);
        var tenantAdminRoleB = CreateRole(TenantWrightIdentityRoleNames.TenantAdmin, tenantB);

        var adminA = CreateUser(tenantA, "tenant-admin-a", "admin-a@example.com");
        userA1 = CreateUser(tenantA, "tenant-user-a1", "user-a1@example.com");
        var lockedA = CreateUser(tenantA, "tenant-user-locked", "locked-a@example.com", lockoutEnd: now.AddHours(8));
        pendingA = CreateUser(tenantA, "tenant-user-pending", "pending-a@example.com", emailConfirmed: false, requireActivation: true);
        var inactiveA = CreateUser(tenantA, "tenant-user-inactive", "inactive-a@example.com", isActive: false);
        var adminB = CreateUser(tenantB, "tenant-admin-b", "admin-b@example.com");
        var userB1 = CreateUser(tenantB, "tenant-user-b1", "user-b1@example.com");
        var hostAdmin = CreateUser(null, "host-admin", "host-admin@example.com");

        dbContext.Roles.AddRange(hostRole, tenantAdminRoleA, tenantAdminRoleB);
        dbContext.Users.AddRange(adminA, userA1, lockedA, pendingA, inactiveA, adminB, userB1, hostAdmin);
        dbContext.UserRoles.AddRange(
            new IdentityUserRole<Guid> { UserId = adminA.Id, RoleId = tenantAdminRoleA.Id },
            new IdentityUserRole<Guid> { UserId = adminB.Id, RoleId = tenantAdminRoleB.Id },
            new IdentityUserRole<Guid> { UserId = hostAdmin.Id, RoleId = hostRole.Id });

        dbContext.IdentitySessions.AddRange(
            CreateSession(adminA.Id, tenantA, "active-admin-a", IdentitySessionStatus.Active, now.AddHours(-2), now.AddHours(-1), null),
            CreateSession(userA1.Id, tenantA, "active-user-a1", IdentitySessionStatus.Active, now.AddHours(-3), now.AddHours(-1), null),
            CreateSession(lockedA.Id, tenantA, "revoked-user-a", IdentitySessionStatus.Revoked, now.AddHours(-6), now.AddHours(-2), now.AddHours(-2)),
            CreateSession(adminB.Id, tenantB, "active-admin-b", IdentitySessionStatus.Active, now.AddHours(-4), now.AddHours(-1), null),
            CreateSession(userB1.Id, tenantB, "revoked-user-b", IdentitySessionStatus.Revoked, now.AddHours(-7), now.AddHours(-3), now.AddHours(-3)));

        dbContext.IdentityPublicInvitations.AddRange(
            CreateInvitation(tenantA, "Tenant A", "pending-a1@example.com", now.AddDays(-2), now.AddHours(24)),
            CreateInvitation(tenantA, "Tenant A", "pending-a2@example.com", now.AddDays(-1), now.AddHours(60)),
            CreateInvitation(tenantA, "Tenant A", "accepted-a@example.com", now.AddDays(-2), now.AddDays(3), acceptedAtUtc: now.AddHours(-18)),
            CreateInvitation(tenantA, "Tenant A", "canceled-a@example.com", now.AddDays(-2), now.AddDays(2), canceledAtUtc: now.AddHours(-12)),
            CreateInvitation(tenantA, "Tenant A", "expired-a@example.com", now.AddDays(-3), now.AddHours(-6)),
            CreateInvitation(tenantB, "Tenant B", "pending-b1@example.com", now.AddDays(-1), now.AddHours(12)));

        dbContext.IdentitySecurityLogs.AddRange(
            CreateSecurityLog(tenantA, userA1.Id, userA1.UserName!, "LoginFailed", now.AddHours(-2)),
            CreateSecurityLog(tenantA, userA1.Id, userA1.UserName!, "LoginInvalidPassword", now.AddHours(-3)),
            CreateSecurityLog(tenantA, lockedA.Id, lockedA.UserName!, "LoginLockedOut", now.AddHours(-4)),
            CreateSecurityLog(tenantA, pendingA.Id, pendingA.UserName!, "LoginRiskBlocked", now.AddHours(-5)),
            CreateSecurityLog(tenantA, userA1.Id, userA1.UserName!, "TwoFactorChallenge", now.AddHours(-6)),
            CreateSecurityLog(tenantA, adminA.Id, adminA.UserName!, "ManualLock", now.AddHours(-7)),
            CreateSecurityLog(tenantA, adminA.Id, adminA.UserName!, "LogoutSelectedSessionsByAdmin", now.AddHours(-8)),
            CreateSecurityLog(tenantA, userA1.Id, userA1.UserName!, "LoginSuccess", now.AddDays(-2)),
            CreateSecurityLog(tenantB, userB1.Id, userB1.UserName!, "LoginSuccess", now.AddHours(-2)),
            CreateSecurityLog(tenantB, userB1.Id, userB1.UserName!, "LoginRiskBlocked", now.AddHours(-9)),
            CreateSecurityLog(tenantB, adminB.Id, adminB.UserName!, "ManualUnlock", now.AddHours(-10)));

        dbContext.SaveChanges();
    }

    private static IdentityDbContextApp CreateDbContext()
    {
        return IdentityTestSupport.CreateDbContext();
    }

    private static ApplicationRole CreateRole(string roleName, Guid? tenantId)
    {
        return new ApplicationRole(roleName)
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NormalizedName = roleName.ToUpperInvariant()
        };
    }

    private static ApplicationUser CreateUser(
        Guid? tenantId,
        string userName,
        string email,
        bool isActive = true,
        bool emailConfirmed = true,
        bool requireActivation = false,
        DateTime? lockoutEnd = null)
    {
        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId ?? Guid.Empty,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsActive = isActive,
            EmailConfirmed = emailConfirmed,
            RequireEmailActivationBeforeLogin = requireActivation,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            LockoutEnd = lockoutEnd.HasValue ? new DateTimeOffset(lockoutEnd.Value) : null
        };
    }

    private static IdentitySession CreateSession(
        Guid userId,
        Guid tenantId,
        string sessionId,
        IdentitySessionStatus status,
        DateTime createdAtUtc,
        DateTime lastActiveUtc,
        DateTime? revokedAtUtc)
    {
        return new IdentitySession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            SessionId = sessionId,
            CreationTime = createdAtUtc,
            LastActiveTime = lastActiveUtc,
            Status = status,
            RevokedAtUtc = revokedAtUtc,
            RevocationSource = revokedAtUtc.HasValue ? "Seed" : null
        };
    }

    private static IdentityPublicInvitation CreateInvitation(
        Guid tenantId,
        string tenantName,
        string email,
        DateTime createdAtUtc,
        DateTime expiresAtUtc,
        DateTime? acceptedAtUtc = null,
        DateTime? canceledAtUtc = null)
    {
        return new IdentityPublicInvitation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TenantName = tenantName,
            Email = email,
            TokenHash = Guid.NewGuid().ToString("N"),
            RoleName = TenantWrightIdentityRoleNames.TenantAdmin,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            AcceptedAtUtc = acceptedAtUtc,
            CanceledAtUtc = canceledAtUtc
        };
    }

    private static IdentitySecurityLog CreateSecurityLog(Guid tenantId, Guid userId, string userName, string action, DateTime createdAtUtc)
    {
        return new IdentitySecurityLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            UserName = userName,
            Action = action,
            ClientIpAddress = "127.0.0.1",
            CreationTime = createdAtUtc
        };
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

    private sealed class InMemoryHybridCache<TCacheItem> : IHybridCache<TCacheItem, string>
        where TCacheItem : class
    {
        private readonly Dictionary<string, TCacheItem> _items = new(StringComparer.Ordinal);

        public Task<TCacheItem?> GetOrCreateAsync(
            string key,
            Func<Task<TCacheItem>> factory,
            Func<HybridCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            return GetOrCreateCoreAsync(key, factory);
        }

        public Task SetAsync(
            string key,
            TCacheItem value,
            HybridCacheEntryOptions? options = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            _items[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            string key,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            _items.Remove(key);
            return Task.CompletedTask;
        }

        public Task RemoveManyAsync(
            IEnumerable<string> keys,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            foreach (var key in keys)
            {
                _items.Remove(key);
            }

            return Task.CompletedTask;
        }

        private async Task<TCacheItem?> GetOrCreateCoreAsync(string key, Func<Task<TCacheItem>> factory)
        {
            if (_items.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var created = await factory();
            _items[key] = created;
            return created;
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

}
