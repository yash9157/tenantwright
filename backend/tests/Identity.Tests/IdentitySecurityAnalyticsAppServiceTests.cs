using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Account.Risk;
using Identity.Application.Contracts.SecurityAnalytics;
using Identity.Application.SecurityAnalytics;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.Account;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Shared;
using Xunit;

namespace Identity.Tests;

public class IdentitySecurityAnalyticsAppServiceTests
{
    [Fact]
    public async Task GetAdminAsync_should_return_cross_tenant_security_summary()
    {
        var now = DateTime.UtcNow.Date.AddHours(12);
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        SeedAnalyticsData(dbContext, now, out _, out _, out _, out _);

        var service = new IdentitySecurityAnalyticsAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(null),
            IdentityTestSupport.GetDataFilter(dbContext),
            new IdentityTestSupport.InMemoryHybridCache<AdminSecurityAnalyticsDto>(),
            new IdentityTestSupport.InMemoryHybridCache<SecurityTimingHeatmapDto>(),
            new IdentityTestSupport.InMemoryHybridCache<TenantSecurityAnalyticsDto>());

        var analytics = await service.GetAdminAsync(30);

        Assert.Equal(30, analytics.Summary.WindowDays);
        Assert.InRange(analytics.Summary.GeneratedAtUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(4, analytics.Summary.LoginFailuresLast24h);
        Assert.Equal(2, analytics.Summary.LoginLockedOutLast24h);
        Assert.Equal(2, analytics.Summary.RiskBlockedLast24h);
        Assert.Equal(1, analytics.Summary.TwoFactorChallengesLast24h);
        Assert.Equal(1, analytics.Summary.FalsePositivesMarkedLast24h);
        Assert.Equal(3, analytics.Summary.ActiveOverrides);
        Assert.Equal(2, analytics.Summary.ExpiringOverridesNext72h);
        Assert.Equal(30, analytics.DailyTrend.Count);
        Assert.Equal(3, analytics.TopAtRiskUsers.Count);
        Assert.Equal("10.0.0.1", analytics.TopSourceIps[0].ClientIpAddress);
        Assert.Equal(2, analytics.ExpiringOverrides.Count);

        var todayBucket = analytics.DailyTrend.Single(x => x.DateUtc == now.Date);
        Assert.Equal(3, todayBucket.LoginFailedCount);
        Assert.Equal(1, todayBucket.LoginInvalidPasswordCount);
        Assert.Equal(2, todayBucket.LoginLockedOutCount);
        Assert.Equal(2, todayBucket.LoginRiskBlockedCount);
        Assert.Equal(1, todayBucket.TwoFactorChallengeCount);
        Assert.Equal(1, todayBucket.FalsePositiveMarkedCount);
        Assert.Equal(1, todayBucket.OverrideCreatedCount);
    }

    [Fact]
    public async Task GetTenantAsync_should_be_tenant_safe_and_exclude_elevated_user_widgets()
    {
        var now = DateTime.UtcNow.Date.AddHours(12);
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        SeedAnalyticsData(dbContext, now, out var tenantA, out _, out var userA1, out var adminA);

        var service = new IdentitySecurityAnalyticsAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(tenantA),
            IdentityTestSupport.GetDataFilter(dbContext),
            new IdentityTestSupport.InMemoryHybridCache<AdminSecurityAnalyticsDto>(),
            new IdentityTestSupport.InMemoryHybridCache<SecurityTimingHeatmapDto>(),
            new IdentityTestSupport.InMemoryHybridCache<TenantSecurityAnalyticsDto>());

        var analytics = await service.GetTenantAsync(30);

        Assert.Equal(tenantA, analytics.TenantId);
        Assert.Equal(30, analytics.Summary.WindowDays);
        Assert.InRange(analytics.Summary.GeneratedAtUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(2, analytics.Summary.LoginFailuresLast24h);
        Assert.Equal(1, analytics.Summary.LoginLockedOutLast24h);
        Assert.Equal(1, analytics.Summary.RiskBlockedLast24h);
        Assert.Equal(1, analytics.Summary.TwoFactorChallengesLast24h);
        Assert.Equal(1, analytics.Summary.FalsePositivesMarkedLast24h);
        Assert.Equal(1, analytics.Summary.ActiveOverrides);
        Assert.Equal(1, analytics.Summary.ExpiringOverridesNext72h);
        Assert.Single(analytics.TopAtRiskUsers);
        Assert.Equal(userA1.Id, analytics.TopAtRiskUsers[0].UserId);
        Assert.DoesNotContain(analytics.TopAtRiskUsers, x => x.UserId == adminA.Id);
        Assert.Single(analytics.ExpiringOverrides);
        Assert.Equal(userA1.Id, analytics.ExpiringOverrides[0].UserId);
        Assert.DoesNotContain(analytics.ExpiringOverrides, x => x.UserId == adminA.Id);
        Assert.Single(analytics.TopSourceIps);
        Assert.Equal("10.0.0.1", analytics.TopSourceIps[0].ClientIpAddress);
    }

    [Fact]
    public async Task GetTenantAsync_should_fail_closed_for_invalid_window_days()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var tenantId = Guid.NewGuid();
        var service = new IdentitySecurityAnalyticsAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(tenantId),
            IdentityTestSupport.GetDataFilter(dbContext),
            new IdentityTestSupport.InMemoryHybridCache<AdminSecurityAnalyticsDto>(),
            new IdentityTestSupport.InMemoryHybridCache<SecurityTimingHeatmapDto>(),
            new IdentityTestSupport.InMemoryHybridCache<TenantSecurityAnalyticsDto>());

        await Assert.ThrowsAsync<SharedValidationException>(() => service.GetTenantAsync(14));
    }

    private static void SeedAnalyticsData(
        Identity.EntityFrameworkCore.EntityFrameworkCore.IdentityDbContextApp dbContext,
        DateTime now,
        out Guid tenantA,
        out Guid tenantB,
        out ApplicationUser userA1,
        out ApplicationUser adminA)
    {
        tenantA = Guid.NewGuid();
        tenantB = Guid.NewGuid();

        var tenantAdminRoleA = CreateRole(TenantWrightIdentityRoleNames.TenantAdmin, tenantA);
        var tenantAdminRoleB = CreateRole(TenantWrightIdentityRoleNames.TenantAdmin, tenantB);
        var hostAdminRole = CreateRole(TenantWrightIdentityRoleNames.HostAdmin, tenantId: null);

        adminA = CreateUser(tenantA, "tenant-admin-a", "tenant-admin-a@example.com");
        userA1 = CreateUser(tenantA, "tenant-user-a1", "tenant-user-a1@example.com");
        var adminB = CreateUser(tenantB, "tenant-admin-b", "tenant-admin-b@example.com");
        var userB1 = CreateUser(tenantB, "tenant-user-b1", "tenant-user-b1@example.com");
        var hostAdmin = CreateUser(null, "host-admin", "host-admin@example.com");

        dbContext.Roles.AddRange(tenantAdminRoleA, tenantAdminRoleB, hostAdminRole);
        dbContext.Users.AddRange(adminA, userA1, adminB, userB1, hostAdmin);
        dbContext.UserRoles.AddRange(
            new IdentityUserRole<Guid> { UserId = adminA.Id, RoleId = tenantAdminRoleA.Id },
            new IdentityUserRole<Guid> { UserId = adminB.Id, RoleId = tenantAdminRoleB.Id },
            new IdentityUserRole<Guid> { UserId = hostAdmin.Id, RoleId = hostAdminRole.Id });

        dbContext.IdentitySecurityLogs.AddRange(
            CreateSecurityLog(tenantA, userA1.Id, userA1.UserName!, "LoginFailed", now.AddHours(-2), "10.0.0.1"),
            CreateSecurityLog(tenantA, userA1.Id, userA1.UserName!, "LoginInvalidPassword", now.AddHours(-3), "10.0.0.1"),
            CreateSecurityLog(tenantA, userA1.Id, userA1.UserName!, "LoginRiskBlocked", now.AddHours(-4), "10.0.0.1", blockedByRisk: true),
            CreateSecurityLog(tenantA, userA1.Id, userA1.UserName!, "TwoFactorChallenge", now.AddHours(-5), "10.0.0.1"),
            CreateSecurityLog(tenantA, userA1.Id, userA1.UserName!, "LoginLockedOut", now.AddHours(-6), "10.0.0.1"),
            CreateSecurityLog(tenantA, adminA.Id, adminA.UserName!, "LoginFailed", now.AddHours(-1), "10.0.0.99"),
            CreateSecurityLog(tenantA, adminA.Id, adminA.UserName!, "LoginRiskFalsePositiveMarked", now.AddHours(-2), "10.0.0.99"),
            CreateSecurityLog(tenantA, adminA.Id, adminA.UserName!, "LoginRiskOverrideCreated", now.AddHours(-3), "10.0.0.99"),
            CreateSecurityLog(tenantB, userB1.Id, userB1.UserName!, "LoginFailed", now.AddHours(-2), "10.0.1.1"),
            CreateSecurityLog(tenantB, userB1.Id, userB1.UserName!, "LoginRiskBlocked", now.AddHours(-3), "10.0.1.1", blockedByRisk: true),
            CreateSecurityLog(tenantB, userB1.Id, userB1.UserName!, "LoginLockedOut", now.AddHours(-4), "10.0.1.1"));

        dbContext.IdentityLoginRiskOverrides.AddRange(
            CreateOverride(tenantA, userA1.Id, now.AddHours(24)),
            CreateOverride(tenantA, adminA.Id, now.AddHours(36)),
            CreateOverride(tenantB, userB1.Id, now.AddHours(96)),
            CreateOverride(tenantA, userA1.Id, now.AddHours(-1), isActive: false));

        dbContext.SaveChanges();
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

    private static ApplicationUser CreateUser(Guid? tenantId, string userName, string email)
    {
        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId ?? Guid.Empty,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsActive = true,
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
    }

    [Fact]
    public async Task GetAdminTimingHeatmapAsync_should_bucket_success_events_in_ist_and_zero_fill()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var tenant = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var utc = DateTime.UtcNow.Date.AddDays(-1).AddHours(20);
        dbContext.IdentitySecurityLogs.AddRange(
            CreateSecurityLog(tenant, userId, "u", "LoginSuccess", utc, "127.0.0.1"),
            CreateSecurityLog(tenant, userId, "u", "LoginSuccess", utc, "127.0.0.1"),
            CreateSecurityLog(tenant, userId, "u", "LoginFailed", utc, "127.0.0.1"));
        dbContext.SaveChanges();

        var service = new IdentitySecurityAnalyticsAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(null),
            IdentityTestSupport.GetDataFilter(dbContext),
            new IdentityTestSupport.InMemoryHybridCache<AdminSecurityAnalyticsDto>(),
            new IdentityTestSupport.InMemoryHybridCache<SecurityTimingHeatmapDto>(),
            new IdentityTestSupport.InMemoryHybridCache<TenantSecurityAnalyticsDto>());

        var heatmap = await service.GetAdminTimingHeatmapAsync(30, "success");

        Assert.Equal(168, heatmap.Cells.Count);
        Assert.Equal("Asia/Kolkata", heatmap.TimeZone);
        Assert.Equal("success", heatmap.Outcome);
        // Only successes count; the failed event is excluded.
        Assert.Equal(2, heatmap.Cells.Sum(c => c.Count));
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(TimeSpan.FromHours(5.5));
        var cell = heatmap.Cells.Single(c => c.DayOfWeek == (int)local.DayOfWeek && c.Hour == local.Hour);
        Assert.Equal(2, cell.Count);
    }

    private static IdentitySecurityLog CreateSecurityLog(
        Guid tenantId,
        Guid userId,
        string userName,
        string action,
        DateTime createdAtUtc,
        string clientIpAddress,
        bool blockedByRisk = false)
    {
        return new IdentitySecurityLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ApplicationName = "Identity",
            Identity = "Account",
            Action = action,
            UserId = userId,
            UserName = userName,
            ClientIpAddress = clientIpAddress,
            CreationTime = createdAtUtc,
            BlockedByRisk = blockedByRisk,
            RiskAction = blockedByRisk ? LoginRiskAction.Block.ToString() : null,
            RiskLevel = blockedByRisk ? LoginRiskLevel.High.ToString() : null,
            RiskScore = blockedByRisk ? 90 : null
        };
    }

    private static IdentityLoginRiskOverride CreateOverride(Guid tenantId, Guid userId, DateTime? expiresAtUtc, bool isActive = true)
    {
        return new IdentityLoginRiskOverride
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            ModeOverride = IdentityLoginRiskOverrideMode.Allow,
            Reason = "seeded",
            CreationTime = DateTime.UtcNow.AddHours(-1),
            ExpiresAtUtc = expiresAtUtc,
            IsActive = isActive
        };
    }
}
