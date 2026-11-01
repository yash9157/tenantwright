using Identity.Application.LoginRisk;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.LoginRisk;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Xunit;

namespace Identity.Tests;

public class LoginRiskAdminAppServiceTests
{
    [Fact]
    public async Task MarkFalsePositiveAsync_should_update_scalar_flag_and_write_follow_up_log()
    {
        var tenantId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();

        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var blockedLog = new IdentitySecurityLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ApplicationName = "Identity",
            Identity = "Account",
            Action = "LoginRiskBlocked",
            UserId = Guid.NewGuid(),
            UserName = "tenant-user",
            CreationTime = DateTime.UtcNow.AddMinutes(-5),
            ExtraProperties = "{\"riskLevel\":\"High\",\"riskAction\":\"Block\",\"riskScore\":93,\"blockedByRisk\":true}"
        };

        dbContext.IdentitySecurityLogs.Add(blockedLog);
        await dbContext.SaveChangesAsync();

        var service = new LoginRiskAdminAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(tenantId),
            new IdentityTestSupport.TestCurrentUser(currentUserId, tenantId, "tenant-admin", "TenantAdmin"));

        await service.MarkFalsePositiveAsync(blockedLog.Id);

        var updated = await dbContext.IdentitySecurityLogs.FindAsync(blockedLog.Id);
        Assert.NotNull(updated);
        Assert.True(updated!.FalsePositive);
        Assert.NotNull(updated.ExtraProperties);
        Assert.Contains("\"falsePositive\":true", updated.ExtraProperties, StringComparison.Ordinal);

        var followUpLog = dbContext.IdentitySecurityLogs.Single(x => x.Action == "LoginRiskFalsePositiveMarked");
        Assert.Equal(tenantId, followUpLog.TenantId);
        Assert.Equal(currentUserId, followUpLog.UserId);
    }

    [Fact]
    public async Task Host_override_targets_platform_staff_and_returns_display_safe_paged_projection()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var platformStaff = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.Empty,
            UserName = "platform.support",
            NormalizedUserName = "PLATFORM.SUPPORT",
            Email = "support@example.test",
            NormalizedEmail = "SUPPORT@EXAMPLE.TEST",
            IsActive = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
        dbContext.Users.Add(platformStaff);
        await dbContext.SaveChangesAsync();
        var service = new LoginRiskAdminAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.TestCurrentTenant(null),
            new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin));

        await service.CreateOverrideAsync(new LoginRiskOverrideCreateInput
        {
            UserId = platformStaff.Id,
            ModeOverride = IdentityLoginRiskOverrideMode.ForceMfa,
            Reason = "Verified travel exception"
        });
        var result = await service.GetPagedOverridesAsync(new LoginRiskOverrideListInput());

        var entry = Assert.Single(result.Items);
        Assert.Equal("platform.support", entry.UserName);
        Assert.Equal("support@example.test", entry.UserEmail);
        Assert.Equal(1, result.TotalCount);
    }
}
