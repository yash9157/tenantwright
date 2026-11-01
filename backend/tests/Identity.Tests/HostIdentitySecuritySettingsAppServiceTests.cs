using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Account.Risk;
using Identity.Application.Contracts.SecuritySettings;
using Identity.Application.SecuritySettings;
using Identity.Domain.SecuritySettings;
using Identity.Domain.Shared.Account.Risk;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Shared;
using Xunit;

namespace Identity.Tests;

public class HostIdentitySecuritySettingsAppServiceTests
{
    [Fact]
    public async Task GetHostAsync_Should_Return_Defaults_When_Row_Is_Missing()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var staticOptions = CreateStaticOptions();
        var runtimeState = new HostIdentitySecurityPolicyRuntimeState(staticOptions);
        var provider = CreateProvider(dbContext, staticOptions, runtimeState, currentTenant);
        var service = CreateService(
            dbContext,
            staticOptions,
            runtimeState,
            provider,
            currentTenant,
            new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin));

        var result = await service.GetHostAsync();

        Assert.Equal(staticOptions.DefaultSettings.ConcurrencyStamp, result.ConcurrencyStamp);
        Assert.Equal(staticOptions.DefaultSettings.Password.RequiredLength, result.Password.RequiredLength);
        Assert.Equal(staticOptions.DefaultSettings.LoginRisk.HighThreshold, result.LoginRisk.HighThreshold);
        Assert.Equal(staticOptions.DefaultSettings.PublicInvitationManagement.TokenTtlHours, result.PublicInvitationManagement.TokenTtlHours);
    }

    [Fact]
    public async Task UpdateHostAsync_Should_Update_Row_Runtime_State_And_Log_Audit_Event()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var staticOptions = CreateStaticOptions();
        var runtimeState = new HostIdentitySecurityPolicyRuntimeState(staticOptions);
        var provider = CreateProvider(dbContext, staticOptions, runtimeState, currentTenant);
        var hostUser = new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin);
        var service = CreateService(
            dbContext,
            staticOptions,
            runtimeState,
            provider,
            currentTenant,
            hostUser);

        var existingPolicy = HostIdentitySecuritySettingsMapper.CreatePolicy(staticOptions.DefaultSettings);
        await dbContext.HostIdentitySecurityPolicies.AddAsync(existingPolicy);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var input = new UpdateHostIdentitySecuritySettingsInput
        {
            ConcurrencyStamp = existingPolicy.ConcurrencyStamp,
            Password = new HostIdentitySecurityPasswordSettingsDto
            {
                RequiredLength = 12,
                RequireDigit = true,
                RequireLowercase = true,
                RequireUppercase = true,
                RequireNonAlphanumeric = true
            },
            Lockout = new HostIdentitySecurityLockoutSettingsDto
            {
                AllowedForNewUsers = false,
                MaxFailedAccessAttempts = 6,
                DefaultLockoutMinutes = 20
            },
            LoginRisk = new HostIdentityLoginRiskSettingsDto
            {
                Enabled = true,
                Mode = LoginRiskMode.Enforce,
                LowThreshold = 35,
                HighThreshold = 75,
                NewDeviceWeight = 21,
                NewIpWeight = 19,
                GeoShiftWeight = 30,
                FailureBurstWeight = 23,
                FailureBurstWindowMinutes = 10,
                FailureBurstThreshold = 4,
                HistoryWindowDays = 20,
                TrustedDeviceDays = 25
            },
            InteractiveLogin = new HostIdentityInteractiveLoginSettingsDto
            {
                ChallengeTtlSeconds = 120
            },
            LoginAttemptProtection = new HostIdentityLoginAttemptProtectionSettingsDto
            {
                Enabled = true,
                RequestLimitPerWindow = 11,
                RequestLimitWindowSeconds = 150
            },
            PasswordReset = new HostIdentityPasswordResetSettingsDto
            {
                Enabled = false,
                RequestLimitPerWindow = 3,
                RequestLimitWindowSeconds = 180
            },
            EmailVerification = new HostIdentityEmailVerificationSettingsDto
            {
                Enabled = false
            },
            PublicInvitationManagement = new HostIdentityPublicInvitationManagementSettingsDto
            {
                Enabled = true,
                TokenTtlHours = 48
            }
        };

        var result = await service.UpdateHostAsync(input);
        var refreshed = await provider.GetEffectiveAsync();
        var securityLog = dbContext.IdentitySecurityLogs.Single(x => x.Action == "HostSecurityPolicyUpdated");

        Assert.NotEqual(existingPolicy.ConcurrencyStamp, result.ConcurrencyStamp);
        Assert.Equal(12, result.Password.RequiredLength);
        Assert.Equal(LoginRiskMode.Enforce, result.LoginRisk.Mode);
        Assert.Equal(120, result.InteractiveLogin.ChallengeTtlSeconds);
        Assert.Equal(3, refreshed.PasswordReset.RequestLimitPerWindow);
        Assert.Equal(result.ConcurrencyStamp, runtimeState.GetCurrent().ConcurrencyStamp);
        Assert.Equal("SecuritySettings", securityLog.Identity);
        Assert.Equal(hostUser.Id, securityLog.UserId);
        Assert.Contains("\"Password\"", securityLog.ExtraProperties, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateHostAsync_Should_Reject_Stale_ConcurrencyStamp()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var staticOptions = CreateStaticOptions();
        var runtimeState = new HostIdentitySecurityPolicyRuntimeState(staticOptions);
        var provider = CreateProvider(dbContext, staticOptions, runtimeState, currentTenant);
        var service = CreateService(
            dbContext,
            staticOptions,
            runtimeState,
            provider,
            currentTenant,
            new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin));

        var existingPolicy = HostIdentitySecuritySettingsMapper.CreatePolicy(staticOptions.DefaultSettings);
        await dbContext.HostIdentitySecurityPolicies.AddAsync(existingPolicy);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var input = BuildUpdateInput(existingPolicy.ConcurrencyStamp);
        input.ConcurrencyStamp = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<SharedValidationException>(() => service.UpdateHostAsync(input));
    }

    [Fact]
    public async Task Governed_host_update_should_preview_impact_and_record_the_operator_reason()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var staticOptions = CreateStaticOptions();
        var runtimeState = new HostIdentitySecurityPolicyRuntimeState(staticOptions);
        var provider = CreateProvider(dbContext, staticOptions, runtimeState, currentTenant);
        var service = CreateService(
            dbContext,
            staticOptions,
            runtimeState,
            provider,
            currentTenant,
            new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin));

        var existingPolicy = HostIdentitySecuritySettingsMapper.CreatePolicy(staticOptions.DefaultSettings);
        await dbContext.HostIdentitySecurityPolicies.AddAsync(existingPolicy);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var settings = BuildUpdateInput(existingPolicy.ConcurrencyStamp);
        settings.Password.RequiredLength = 14;
        var impact = await service.PreviewHostUpdateAsync(settings);
        var updated = await service.UpdateHostGovernedAsync(new GovernedHostIdentitySecuritySettingsUpdateInput
        {
            Settings = settings,
            Reason = "Increase the platform password baseline"
        });

        var securityLog = dbContext.IdentitySecurityLogs.Single(x => x.Action == "HostSecurityPolicyUpdated");
        Assert.Contains("Password", impact.ChangedSections);
        Assert.True(impact.AffectsFutureSignIns);
        Assert.True(impact.ActiveSessionsRemainValid);
        Assert.Equal(14, updated.Password.RequiredLength);
        Assert.Contains("Increase the platform password baseline", securityLog.ExtraProperties, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostSecuritySettings_Service_Should_Fail_Closed_For_Tenant_Scope_And_Non_Admin()
    {
        var tenantScopedCurrentTenant = new IdentityTestSupport.TestCurrentTenant(Guid.NewGuid(), "tenant-a");
        await using var dbContext = IdentityTestSupport.CreateDbContext(tenantScopedCurrentTenant);
        var staticOptions = CreateStaticOptions();
        var runtimeState = new HostIdentitySecurityPolicyRuntimeState(staticOptions);
        var provider = CreateProvider(dbContext, staticOptions, runtimeState, tenantScopedCurrentTenant);

        var tenantScopedService = CreateService(
            dbContext,
            staticOptions,
            runtimeState,
            provider,
            tenantScopedCurrentTenant,
            new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "host.admin", TenantWrightIdentityRoleNames.HostAdmin));

        var hostCurrentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        var nonAdminService = CreateService(
            dbContext,
            staticOptions,
            runtimeState,
            CreateProvider(dbContext, staticOptions, runtimeState, hostCurrentTenant),
            hostCurrentTenant,
            new IdentityTestSupport.TestCurrentUser(Guid.NewGuid(), null, "user"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tenantScopedService.GetHostAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => nonAdminService.GetHostAsync());
    }

    [Fact]
    public void Dynamic_Option_Accessors_Should_Use_Runtime_Settings_And_Preserve_Static_Extras()
    {
        var staticOptions = CreateStaticOptions();
        var runtimeState = new HostIdentitySecurityPolicyRuntimeState(staticOptions);
        runtimeState.SetCurrent(new HostIdentitySecuritySettingsDto
        {
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            Password = new HostIdentitySecurityPasswordSettingsDto
            {
                RequiredLength = 14,
                RequireDigit = true,
                RequireLowercase = true,
                RequireUppercase = false,
                RequireNonAlphanumeric = false
            },
            Lockout = new HostIdentitySecurityLockoutSettingsDto
            {
                AllowedForNewUsers = true,
                MaxFailedAccessAttempts = 8,
                DefaultLockoutMinutes = 45
            },
            LoginRisk = new HostIdentityLoginRiskSettingsDto
            {
                Enabled = true,
                Mode = LoginRiskMode.Observe,
                LowThreshold = 25,
                HighThreshold = 55,
                NewDeviceWeight = 10,
                NewIpWeight = 11,
                GeoShiftWeight = 12,
                FailureBurstWeight = 13,
                FailureBurstWindowMinutes = 14,
                FailureBurstThreshold = 2,
                HistoryWindowDays = 16,
                TrustedDeviceDays = 17
            },
            InteractiveLogin = new HostIdentityInteractiveLoginSettingsDto
            {
                ChallengeTtlSeconds = 200
            },
            LoginAttemptProtection = new HostIdentityLoginAttemptProtectionSettingsDto
            {
                Enabled = false,
                RequestLimitPerWindow = 9,
                RequestLimitWindowSeconds = 99
            },
            PasswordReset = new HostIdentityPasswordResetSettingsDto
            {
                Enabled = true,
                RequestLimitPerWindow = 4,
                RequestLimitWindowSeconds = 240
            },
            EmailVerification = new HostIdentityEmailVerificationSettingsDto
            {
                Enabled = false
            },
            PublicInvitationManagement = new HostIdentityPublicInvitationManagementSettingsDto
            {
                Enabled = false,
                TokenTtlHours = 36
            }
        });

        var identityOptions = new HostIdentityOptionsAccessor(runtimeState).Value;
        var loginRiskOptions = new HostLoginRiskOptionsAccessor(runtimeState, staticOptions).Value;
        var passwordResetOptions = new HostPasswordResetOptionsAccessor(runtimeState, staticOptions).Value;
        var invitationOptions = new HostPublicInvitationManagementOptionsAccessor(runtimeState, staticOptions).Value;

        Assert.Equal(14, identityOptions.Password.RequiredLength);
        Assert.Equal(8, identityOptions.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(14), loginRiskOptions.FailureBurstWindow);
        Assert.Equal(staticOptions.LoginRiskHeaderCountryCodeName, loginRiskOptions.HeaderCountryCodeName);
        Assert.Equal(240, passwordResetOptions.RequestLimitWindowSeconds);
        Assert.Equal(staticOptions.PasswordResetTemplateName, passwordResetOptions.TemplateName);
        Assert.False(invitationOptions.Enabled);
        Assert.Equal(36, invitationOptions.TokenTtlHours);
        Assert.Equal(staticOptions.PublicInvitationSubject, invitationOptions.Subject);
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
                    RequiredLength = 8,
                    RequireDigit = true,
                    RequireLowercase = true,
                    RequireUppercase = true,
                    RequireNonAlphanumeric = true
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

    private static UpdateHostIdentitySecuritySettingsInput BuildUpdateInput(string concurrencyStamp)
    {
        return new UpdateHostIdentitySecuritySettingsInput
        {
            ConcurrencyStamp = concurrencyStamp,
            Password = new HostIdentitySecurityPasswordSettingsDto
            {
                RequiredLength = 10,
                RequireDigit = true,
                RequireLowercase = true,
                RequireUppercase = true,
                RequireNonAlphanumeric = true
            },
            Lockout = new HostIdentitySecurityLockoutSettingsDto
            {
                AllowedForNewUsers = true,
                MaxFailedAccessAttempts = 5,
                DefaultLockoutMinutes = 30
            },
            LoginRisk = new HostIdentityLoginRiskSettingsDto
            {
                Enabled = true,
                Mode = LoginRiskMode.Observe,
                LowThreshold = 35,
                HighThreshold = 75,
                NewDeviceWeight = 20,
                NewIpWeight = 20,
                GeoShiftWeight = 20,
                FailureBurstWeight = 20,
                FailureBurstWindowMinutes = 10,
                FailureBurstThreshold = 2,
                HistoryWindowDays = 20,
                TrustedDeviceDays = 20
            },
            InteractiveLogin = new HostIdentityInteractiveLoginSettingsDto
            {
                ChallengeTtlSeconds = 120
            },
            LoginAttemptProtection = new HostIdentityLoginAttemptProtectionSettingsDto
            {
                Enabled = true,
                RequestLimitPerWindow = 10,
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
        };
    }

    private static HostIdentitySecurityPolicyProvider CreateProvider(
        IdentityDbContextApp dbContext,
        HostIdentitySecurityStaticOptions staticOptions,
        HostIdentitySecurityPolicyRuntimeState runtimeState,
        IdentityTestSupport.TestCurrentTenant currentTenant)
    {
        return new HostIdentitySecurityPolicyProvider(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            currentTenant,
            new IdentityTestSupport.InMemoryHybridCache<HostIdentitySecuritySettingsDto>(),
            staticOptions,
            runtimeState);
    }

    private static HostIdentitySecuritySettingsAppService CreateService(
        IdentityDbContextApp dbContext,
        HostIdentitySecurityStaticOptions staticOptions,
        HostIdentitySecurityPolicyRuntimeState runtimeState,
        IHostIdentitySecurityPolicyProvider provider,
        IdentityTestSupport.TestCurrentTenant currentTenant,
        IdentityTestSupport.TestCurrentUser currentUser)
    {
        return new HostIdentitySecuritySettingsAppService(
            IdentityTestSupport.AsDbContextProvider(dbContext),
            currentTenant,
            currentUser,
            staticOptions,
            runtimeState,
            provider);
    }
}
