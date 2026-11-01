using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.SecurityLogs;
using Identity.Application.SecurityLogs;
using Identity.Domain.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Identity.Tests;

public class SecurityLogAppServiceTests
{
    [Fact]
    public async Task GetPagedListAsync_should_allow_host_admin_cross_tenant_and_apply_new_filters()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        SeedSecurityLogData(dbContext, out _, out _, out _, out _);

        var service = new SecurityLogAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(null),
            new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "host-admin", TenantWrightIdentityRoleNames.HostAdmin));

        var result = await service.GetPagedListAsync(new SecurityLogListInput
        {
            FalsePositive = true,
            ApplicationName = "Identity",
            Identity = "Account",
            ClientIpAddress = "10.10.10.11"
        });

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.True(result.Items[0].FalsePositive);
        Assert.Equal("High", result.Items[0].RiskLevel);
        Assert.Equal("Block", result.Items[0].RiskAction);
    }

    [Fact]
    public async Task GetPagedListAsync_should_exclude_elevated_logs_for_tenant_admin()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        SeedSecurityLogData(dbContext, out var tenantA, out _, out var elevatedUser, out var tenantUser);

        var service = new SecurityLogAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(tenantA),
            new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), tenantA, "tenant-admin", TenantWrightIdentityRoleNames.TenantAdmin));

        var result = await service.GetPagedListAsync(new SecurityLogListInput());

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Items, item => Assert.NotEqual(elevatedUser.Id, item.UserId));
        Assert.Contains(result.Items, item => item.UserId == tenantUser.Id);
    }

    [Fact]
    public async Task GetPagedListAsync_should_default_deny_without_authenticated_tenant_or_host_admin()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        SeedSecurityLogData(dbContext, out _, out _, out _, out _);

        var service = new SecurityLogAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(null),
            new IdentityTestSupport.TestCurrentUser(null, null, null));

        var result = await service.GetPagedListAsync(new SecurityLogListInput());

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetDetailAsync_should_return_forensic_fields_only_for_a_scoped_log()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        SeedSecurityLogData(dbContext, out var tenantA, out _, out _, out _);
        var log = dbContext.IdentitySecurityLogs.IgnoreQueryFilters().First(x => x.TenantId == tenantA);
        log.SessionId = "session-1";
        log.CorrelationId = "correlation-1";
        log.BrowserInfo = "Chrome on Windows";
        log.Location = "Kolkata, IN";
        await dbContext.SaveChangesAsync();
        var service = new SecurityLogAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(tenantA),
            new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), tenantA, "tenant.operator"));

        var detail = await service.GetDetailAsync(log.Id);

        Assert.Equal("session-1", detail.SessionId);
        Assert.Equal("correlation-1", detail.CorrelationId);
        Assert.Equal("Chrome on Windows", detail.BrowserInfo);
        Assert.Equal("Kolkata, IN", detail.Location);
    }

    private static void SeedSecurityLogData(
        Identity.EntityFrameworkCore.EntityFrameworkCore.IdentityDbContextApp dbContext,
        out Guid tenantA,
        out Guid tenantB,
        out ApplicationUser elevatedUser,
        out ApplicationUser tenantUser)
    {
        tenantA = Guid.NewGuid();
        tenantB = Guid.NewGuid();

        var tenantAdminRole = new ApplicationRole(TenantWrightIdentityRoleNames.TenantAdmin)
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            NormalizedName = TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant()
        };

        elevatedUser = CreateUser(tenantA, "tenant-admin-a", "tenant-admin-a@example.com");
        tenantUser = CreateUser(tenantA, "tenant-user-a", "tenant-user-a@example.com");
        var tenantUserB = CreateUser(tenantB, "tenant-user-b", "tenant-user-b@example.com");

        dbContext.Roles.Add(tenantAdminRole);
        dbContext.Users.AddRange(elevatedUser, tenantUser, tenantUserB);
        dbContext.UserRoles.Add(new IdentityUserRole<Guid> { UserId = elevatedUser.Id, RoleId = tenantAdminRole.Id });

        dbContext.IdentitySecurityLogs.AddRange(
            new IdentitySecurityLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantA,
                ApplicationName = "Identity",
                Identity = "Account",
                Action = "LoginRiskBlocked",
                UserId = elevatedUser.Id,
                UserName = elevatedUser.UserName,
                ClientIpAddress = "10.10.10.10",
                CreationTime = DateTime.UtcNow.AddMinutes(-10),
                RiskLevel = "High",
                RiskAction = "Block",
                RiskScore = 95,
                BlockedByRisk = true
            },
            new IdentitySecurityLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantA,
                ApplicationName = "Identity",
                Identity = "Account",
                Action = "LoginRiskBlocked",
                UserId = tenantUser.Id,
                UserName = tenantUser.UserName,
                ClientIpAddress = "10.10.10.10",
                CreationTime = DateTime.UtcNow.AddMinutes(-9),
                RiskLevel = "High",
                RiskAction = "Block",
                RiskScore = 92,
                BlockedByRisk = true
            },
            new IdentitySecurityLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantA,
                ApplicationName = "Identity",
                Identity = "Account",
                Action = "LoginRiskBlocked",
                UserId = tenantUser.Id,
                UserName = tenantUser.UserName,
                ClientIpAddress = "10.10.10.11",
                CreationTime = DateTime.UtcNow.AddMinutes(-8),
                RiskLevel = "High",
                RiskAction = "Block",
                RiskScore = 88,
                BlockedByRisk = true,
                FalsePositive = true,
                CountryCode = "IN",
                ExtraProperties = "{\"riskLevel\":\"High\",\"riskAction\":\"Block\",\"riskScore\":88,\"riskReasons\":[\"new_ip\"],\"blockedByRisk\":true,\"falsePositive\":true,\"countryCode\":\"IN\"}"
            },
            new IdentitySecurityLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantB,
                ApplicationName = "Identity",
                Identity = "Account",
                Action = "LoginFailed",
                UserId = tenantUserB.Id,
                UserName = tenantUserB.UserName,
                ClientIpAddress = "10.20.20.20",
                CreationTime = DateTime.UtcNow.AddMinutes(-7)
            });

        dbContext.SaveChanges();
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
            IsActive = true,
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
    }
}
