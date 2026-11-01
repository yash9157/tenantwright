using Identity.Application.Account.Risk;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Account.Risk;
using Identity.Application.Contracts.SecuritySettings;
using Identity.Application.SecuritySettings;
using Identity.Data;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.SecuritySettings;
using Identity.Domain.Shared.Account.Risk;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Data.Seeding;
using Xunit;

namespace Identity.Tests;

public class DefaultIdentityDataSeedContributorTests
{
    private static readonly Guid DefaultTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task SeedAsync_Should_Reset_Existing_Host_Admin_Password_And_Remain_Idempotent()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var initialOptions = CreateOptions();
        initialOptions.HostAdmin = new IdentityBootstrapHostAdminOptions
        {
            Enabled = true,
            UserName = "hostadmin",
            Email = "hostadmin@example.test",
            Password = "oldpass"
        };
        await CreateContributor(dbContext, currentTenant, initialOptions).SeedAsync(new DataSeedContext());

        var updatedOptions = CreateOptions();
        updatedOptions.HostAdmin = new IdentityBootstrapHostAdminOptions
        {
            Enabled = true,
            UserName = "hostadmin",
            Email = "hostadmin@example.test",
            Password = "123qwe",
            ResetPasswordOnSeed = true
        };
        var updatedContributor = CreateContributor(dbContext, currentTenant, updatedOptions);

        await updatedContributor.SeedAsync(new DataSeedContext());
        await updatedContributor.SeedAsync(new DataSeedContext());

        var userManager = CreateUserManager(dbContext, currentTenant);
        var user = await dbContext.Users.SingleAsync(x => x.UserName == "hostadmin");
        Assert.True(await userManager.CheckPasswordAsync(user, "123qwe"));
        Assert.False(await userManager.CheckPasswordAsync(user, "oldpass"));
        Assert.Equal(1, await dbContext.Users.CountAsync(x => x.UserName == "hostadmin"));
        Assert.Equal(1, await dbContext.UserRoles.CountAsync(x => x.UserId == user.Id));
    }

    [Fact]
    public async Task SeedAsync_Should_Create_Tenant_Admin_When_Host_Admin_Config_Is_Disabled_And_Blank()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var hostRole = new ApplicationRole(TenantWrightIdentityRoleNames.TenantAdmin)
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            NormalizedName = TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant(),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
        await dbContext.Roles.AddAsync(hostRole);
        await dbContext.SaveChangesAsync();

        var contributor = CreateContributor(dbContext, currentTenant, CreateOptions(resetPasswordOnSeed: false));

        await contributor.SeedAsync(new DataSeedContext());

        var user = await dbContext.Users.IgnoreQueryFilters().SingleAsync(x => x.UserName == "tenantadmin");
        var normalizedTenantAdminRoleName = TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant();
        var tenantRole = await dbContext.Roles.IgnoreQueryFilters().SingleAsync(x =>
            x.TenantId == DefaultTenantId &&
            x.NormalizedName == normalizedTenantAdminRoleName);
        var userRole = await dbContext.UserRoles.IgnoreQueryFilters().SingleAsync(x => x.UserId == user.Id);

        Assert.Equal(DefaultTenantId, user.TenantId);
        Assert.Equal("tenantadmin@example.test", user.Email);
        Assert.True(user.EmailConfirmed);
        Assert.True(user.IsActive);
        Assert.Equal(tenantRole.Id, userRole.RoleId);
        Assert.Equal(IdentitySystemRoleIds.TenantAdmin(DefaultTenantId), tenantRole.Id);
        Assert.NotEqual(hostRole.Id, userRole.RoleId);
    }

    [Fact]
    public async Task SeedAsync_Should_Update_Existing_Tenant_Admin_Reset_Password_And_Remain_Idempotent()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var initialContributor = CreateContributor(
            dbContext,
            currentTenant,
            CreateOptions(password: "oldpass", email: "old-tenantadmin@example.test", resetPasswordOnSeed: false));

        await initialContributor.SeedAsync(new DataSeedContext());

        var updateContributor = CreateContributor(
            dbContext,
            currentTenant,
            CreateOptions(password: "123qwe", email: "tenantadmin@example.test", resetPasswordOnSeed: true));
        await updateContributor.SeedAsync(new DataSeedContext());
        await updateContributor.SeedAsync(new DataSeedContext());

        var userManager = CreateUserManager(dbContext, currentTenant);
        var user = await dbContext.Users.IgnoreQueryFilters().SingleAsync(x => x.UserName == "tenantadmin");

        Assert.Equal("tenantadmin@example.test", user.Email);
        Assert.True(await userManager.CheckPasswordAsync(user, "123qwe"));
        Assert.False(await userManager.CheckPasswordAsync(user, "oldpass"));
        Assert.Equal(1, await dbContext.Users.IgnoreQueryFilters().CountAsync(x => x.UserName == "tenantadmin"));
        Assert.Equal(1, await dbContext.UserRoles.IgnoreQueryFilters().CountAsync(x => x.UserId == user.Id));
        var normalizedTenantAdminRoleName = TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant();
        Assert.Equal(1, await dbContext.Roles.IgnoreQueryFilters().CountAsync(x =>
            x.TenantId == DefaultTenantId &&
            x.NormalizedName == normalizedTenantAdminRoleName));
    }

    private static DefaultIdentityDataSeedContributor CreateContributor(
        Identity.EntityFrameworkCore.EntityFrameworkCore.IdentityDbContextApp dbContext,
        IdentityTestSupport.TestCurrentTenant currentTenant,
        IdentityBootstrapSeedOptions options)
    {
        var userManager = CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var staticOptions = CreateStaticOptions();

        return new DefaultIdentityDataSeedContributor(
            userManager,
            roleManager,
            currentTenant,
            Options.Create(options),
            IdentityTestSupport.AsDbContextProvider(dbContext),
            staticOptions,
            new HostIdentitySecurityPolicyRuntimeState(staticOptions));
    }

    private static Identity.Application.ApplicationUsers.ApplicationUserManager CreateUserManager(
        Identity.EntityFrameworkCore.EntityFrameworkCore.IdentityDbContextApp dbContext,
        IdentityTestSupport.TestCurrentTenant currentTenant)
    {
        var identityOptions = new IdentityOptions
        {
            Password =
            {
                RequiredLength = 6,
                RequireDigit = false,
                RequireLowercase = false,
                RequireUppercase = false,
                RequireNonAlphanumeric = false
            }
        };

        return IdentityTestSupport.CreateUserManager(dbContext, currentTenant, identityOptions);
    }

    private static IdentityBootstrapSeedOptions CreateOptions(
        string password = "123qwe",
        string email = "tenantadmin@example.test",
        bool resetPasswordOnSeed = true)
    {
        return new IdentityBootstrapSeedOptions
        {
            Enabled = true,
            HostAdmin = new IdentityBootstrapHostAdminOptions
            {
                Enabled = false,
                TenantId = Guid.Empty,
                UserName = string.Empty,
                Email = string.Empty,
                Password = string.Empty
            },
            TenantAdmin = new IdentityBootstrapTenantAdminOptions
            {
                Enabled = true,
                TenantId = DefaultTenantId,
                TenantName = "default",
                UserName = "tenantadmin",
                Email = email,
                Password = password,
                ResetPasswordOnSeed = resetPasswordOnSeed
            }
        };
    }

    private static HostIdentitySecurityStaticOptions CreateStaticOptions()
    {
        return new HostIdentitySecurityStaticOptions
        {
            DefaultSettings = new HostIdentitySecuritySettingsDto
            {
                ConcurrencyStamp = HostIdentitySecurityPolicy.SingletonId.ToString("N"),
                Password = new HostIdentitySecurityPasswordSettingsDto
                {
                    RequiredLength = 6,
                    RequireDigit = false,
                    RequireLowercase = false,
                    RequireUppercase = false,
                    RequireNonAlphanumeric = false
                },
                Lockout = new HostIdentitySecurityLockoutSettingsDto
                {
                    AllowedForNewUsers = true,
                    MaxFailedAccessAttempts = 5,
                    DefaultLockoutMinutes = 15
                },
                LoginRisk = new HostIdentityLoginRiskSettingsDto
                {
                    Enabled = true,
                    Mode = LoginRiskMode.Observe,
                    LowThreshold = 40,
                    HighThreshold = 70,
                    NewDeviceWeight = 25,
                    NewIpWeight = 20,
                    GeoShiftWeight = 30,
                    FailureBurstWeight = 25,
                    FailureBurstWindowMinutes = 15,
                    FailureBurstThreshold = 3,
                    HistoryWindowDays = 30,
                    TrustedDeviceDays = 30
                },
                InteractiveLogin = new HostIdentityInteractiveLoginSettingsDto
                {
                    ChallengeTtlSeconds = 300
                },
                LoginAttemptProtection = new HostIdentityLoginAttemptProtectionSettingsDto
                {
                    Enabled = true,
                    RequestLimitPerWindow = 15,
                    RequestLimitWindowSeconds = 300
                },
                PasswordReset = new HostIdentityPasswordResetSettingsDto
                {
                    Enabled = true,
                    RequestLimitPerWindow = 5,
                    RequestLimitWindowSeconds = 300
                },
                EmailVerification = new HostIdentityEmailVerificationSettingsDto
                {
                    Enabled = true
                },
                PublicInvitationManagement = new HostIdentityPublicInvitationManagementSettingsDto
                {
                    Enabled = true,
                    TokenTtlHours = 168
                }
            },
            LoginRiskHeaderCountryCodeName = "X-Country-Code",
            PasswordResetTokenTtlMinutes = 15,
            PasswordResetTemplateName = "Identity.PasswordResetLink",
            PasswordResetAllowedBaseUrls = ["https://localhost:7239"],
            EmailVerificationTemplateName = "Identity.EmailVerificationLink",
            EmailVerificationAllowedBaseUrls = ["https://localhost:7239"],
            PublicInvitationTemplateName = "Identity.PublicInvitationLink",
            PublicInvitationSubject = "Your organization invited you to join TenantWright",
            PublicInvitationAllowedBaseUrls = ["https://localhost:7239"]
        };
    }
}
