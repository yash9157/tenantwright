using Identity.Application.Account;
using Identity.Application.Account.Risk;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Account.Risk;
using Identity.Application.Contracts.Caching;
using Identity.Application.Contracts.Users;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.Account;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Caching.Abstractions;
using Shared.Caching.Sessions;
using Shared.DistributedLocking.Abstractions;
using Shared.MultiTenancy;
using Xunit;

namespace Identity.Tests;

public sealed class AccountAppServicePlatformLoginTests
{
    [Fact]
    public async Task CheckPasswordAsync_privileged_user_without_enabled_factor_should_succeed()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        await CreateRoleAsync(roleManager, TenantWrightIdentityRoleNames.HostAdmin);
        var user = await CreateUserAsync(userManager, "optional.factor.admin", Guid.Empty, TenantWrightIdentityRoleNames.HostAdmin);
        var signInManager = new StubSignInManager(userManager, SignInResult.Success);
        var service = CreateService(dbContext, currentTenant, userManager, roleManager, signInManager: signInManager);

        var result = await service.CheckPasswordAsync(
            user.UserName!,
            "Pass123$",
            mode: InteractiveLoginMode.Platform,
            sessionId: Guid.NewGuid().ToString("D"));

        Assert.True(result.Succeeded);
        Assert.False(result.RequiresTwoFactor);
        Assert.False(result.RequiresMfaEnrollment);
    }

    [Fact]
    public async Task CheckPasswordAsync_user_with_enrolled_passkey_should_require_two_factor()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var user = await CreateUserAsync(userManager, "passkey.enabled.user", Guid.Empty);
        dbContext.IdentityPasskeyCredentials.Add(new IdentityPasskeyCredential
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            UserId = user.Id,
            CredentialIdHash = Guid.NewGuid().ToString("N"),
            CredentialId = [1],
            PublicKey = [2],
            UserHandle = [3],
            SignatureCounter = 0,
            AaGuid = Guid.NewGuid(),
            FriendlyName = "Phone passkey",
            CreatedAtUtc = DateTime.UtcNow,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        });
        await dbContext.SaveChangesAsync();
        var signInManager = new StubSignInManager(userManager, SignInResult.Success);
        var service = CreateService(dbContext, currentTenant, userManager, roleManager, signInManager: signInManager);

        var result = await service.CheckPasswordAsync(
            user.UserName!,
            "Pass123$",
            mode: InteractiveLoginMode.Platform,
            sessionId: Guid.NewGuid().ToString("D"));

        Assert.False(result.Succeeded);
        Assert.True(result.RequiresTwoFactor);
        Assert.False(result.RequiresMfaEnrollment);
    }

    [Fact]
    public async Task GetUserInfoAsync_platform_mode_returns_host_user_without_tenant_id()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var role = await CreateRoleAsync(roleManager, "PlatformSupport");
        var hostUser = await CreateUserAsync(userManager, "platform.staff", Guid.Empty, "PlatformSupport");
        var service = CreateService(dbContext, currentTenant, userManager, roleManager);

        var userInfo = await service.GetUserInfoAsync(
            "platform.staff",
            tenantId: null,
            mode: InteractiveLoginMode.Platform);

        Assert.NotNull(userInfo);
        Assert.Equal(hostUser.Id, userInfo.Id);
        Assert.Null(userInfo.TenantId);
        Assert.Equal(["PlatformSupport"], userInfo.Roles);
        Assert.Equal([role.Id], userInfo.RoleIds);
    }

    [Fact]
    public async Task GetUserInfoAsync_excludes_retiring_roles_from_names_ids_and_authorization_version()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var role = await CreateRoleAsync(roleManager, "PlatformSupport");
        await CreateUserAsync(userManager, "retiring.role.user", Guid.Empty, "PlatformSupport");
        var service = CreateService(dbContext, currentTenant, userManager, roleManager);
        var before = await service.GetUserInfoAsync(
            "retiring.role.user", tenantId: null, mode: InteractiveLoginMode.Platform);

        role.BeginRetirement(Guid.NewGuid(), DateTime.UtcNow);
        await dbContext.SaveChangesAsync();
        var after = await service.GetUserInfoAsync(
            "retiring.role.user", tenantId: null, mode: InteractiveLoginMode.Platform);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Single(before.RoleIds);
        Assert.Empty(after.RoleIds);
        Assert.Empty(after.Roles);
        Assert.NotEqual(before.AuthzVersion, after.AuthzVersion);
    }

    [Fact]
    public async Task GetUserInfoAsync_platform_mode_does_not_return_tenant_user()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        await CreateUserAsync(userManager, "tenant.only", tenantId);
        var service = CreateService(dbContext, currentTenant, userManager, roleManager);

        var userInfo = await service.GetUserInfoAsync(
            "tenant.only",
            tenantId: null,
            mode: InteractiveLoginMode.Platform);

        Assert.Null(userInfo);
    }

    [Fact]
    public async Task GetUserInfoAsync_should_fail_closed_when_lifecycle_and_legacy_flag_drift()
    {
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var hostUser = await CreateUserAsync(userManager, "suspended.staff", Guid.Empty);
        hostUser.InitializeLifecycle(isActive: false);
        hostUser.IsActive = true; // Simulate a stale compatibility writer.
        var update = await userManager.UpdateAsync(hostUser);
        Assert.True(update.Succeeded);
        var service = CreateService(dbContext, currentTenant, userManager, roleManager);

        var userInfo = await service.GetUserInfoAsync(
            hostUser.UserName!,
            tenantId: null,
            mode: InteractiveLoginMode.Platform);

        Assert.NotNull(userInfo);
        Assert.False(userInfo.IsActive);
        Assert.Equal(IdentityUserLifecycleStatus.Suspended, hostUser.GetEffectiveLifecycleStatus());
    }

    [Fact]
    public async Task GetUserInfoAsync_uses_requested_partition_when_host_and_tenant_aliases_overlap()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(null);
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var hostUser = await CreateUserAsync(userManager, "shared.login", Guid.Empty);
        var tenantUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = "shared.login",
            NormalizedUserName = "SHARED.LOGIN",
            Email = "shared.login@example.test",
            NormalizedEmail = "SHARED.LOGIN@EXAMPLE.TEST",
            EmailConfirmed = true,
            IsActive = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
        dbContext.Users.Add(tenantUser);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, currentTenant, userManager, roleManager);

        var platformInfo = await service.GetUserInfoAsync(
            "shared.login",
            tenantId: null,
            mode: InteractiveLoginMode.Platform);
        using var tenantScope = currentTenant.Change(tenantId);
        var tenantInfo = await service.GetUserInfoAsync(
            "shared.login",
            tenantId,
            mode: InteractiveLoginMode.Tenant);

        Assert.NotNull(platformInfo);
        Assert.NotNull(tenantInfo);
        Assert.Equal(hostUser.Id, platformInfo.Id);
        Assert.Null(platformInfo.TenantId);
        Assert.Equal(tenantUser.Id, tenantInfo.Id);
        Assert.Equal(tenantId, tenantInfo.TenantId);
    }

    [Fact]
    public async Task CompleteMfaAsync_rejects_challenge_when_mode_or_tenant_partition_differs()
    {
        var tenantId = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var challengeCache = new IdentityTestSupport.InMemoryDistributedCache<InteractiveLoginChallengeCacheItem>();
        await challengeCache.SetAsync(
            $"identity:interactive:challenge:{challengeId:D}",
            new InteractiveLoginChallengeCacheItem
            {
                ChallengeId = challengeId,
                Mode = InteractiveLoginMode.Platform,
                TenantId = null,
                ExpectedStep = InteractiveLoginStep.RequiresMfa,
                LoginInput = "platform.staff",
                SessionId = Guid.NewGuid().ToString("N"),
                ClientId = "tenantwright-gateway",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            });
        var service = CreateService(dbContext, currentTenant, userManager, roleManager, challengeCache);

        var result = await service.CompleteMfaAsync(new CompleteMfaInput
        {
            Mode = InteractiveLoginMode.Tenant,
            TenantId = tenantId,
            ChallengeId = challengeId,
            TwoFactorCode = "123456"
        });

        Assert.Equal(InteractiveLoginStep.Failed, result.Step);
        Assert.Null(await challengeCache.GetAsync($"identity:interactive:challenge:{challengeId:D}"));
    }

    private static AccountAppService CreateService(
        IdentityDbContextApp dbContext,
        ICurrentTenant currentTenant,
        ApplicationUserManager userManager,
        ApplicationRoleManager roleManager,
        IDistributedCache<InteractiveLoginChallengeCacheItem>? challengeCache = null,
        SignInManager<ApplicationUser>? signInManager = null)
    {
        var notificationPublisher = new IdentityTestSupport.CapturingMessageSender();
        return new AccountAppService(
            userManager,
            signInManager: signInManager!,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.InMemoryDistributedCache<IdentitySessionValidityCacheItem>(),
            new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            new NoopSessionMarkerStore(),
            Options.Create(new SessionValidationOptions()),
            new IdentityTestSupport.NoopIdentitySessionRevocationService(),
            new AllowLoginRiskEvaluator(),
            Options.Create(new LoginRiskOptions()),
            Options.Create(new LoginRiskAlertingOptions()),
            Options.Create(new PasswordResetOptions()),
            Options.Create(new EmailVerificationOptions()),
            Options.Create(new InteractiveLoginOptions()),
            Options.Create(new LoginAttemptProtectionOptions()),
            new IdentityUserSecurityAlertPublisher(notificationPublisher, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityUserSecurityAlertPublisher>.Instance),
            new IdentityTestSupport.InMemoryDistributedCache<AccountAppService.PasswordResetQuotaState>(),
            new IdentityTestSupport.InMemoryDistributedCache<AccountAppService.LoginAttemptQuotaState>(),
            challengeCache ?? new IdentityTestSupport.InMemoryDistributedCache<InteractiveLoginChallengeCacheItem>(),
            new IdentityTestSupport.NoopDistributedLock(),
            roleManager,
            currentTenant,
            eventBus: notificationPublisher);
    }

    private static async Task<ApplicationRole> CreateRoleAsync(ApplicationRoleManager roleManager, string roleName)
    {
        var role = new ApplicationRole(roleName)
        {
            TenantId = null
        };
        var result = await roleManager.CreateAsync(role);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(x => x.Description)));
        return role;
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        ApplicationUserManager userManager,
        string userName,
        Guid tenantId,
        params string[] roles)
    {
        var user = new ApplicationUser
        {
            UserName = userName,
            Email = $"{userName}@example.test",
            TenantId = tenantId,
            EmailConfirmed = true,
            IsActive = true
        };
        user.InitializeLifecycle(isActive: true);
        var result = await userManager.CreateAsync(user, "Pass123$");
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(x => x.Description)));

        if (roles.Length > 0)
        {
            var addRoles = await userManager.AddToRolesAsync(user, roles);
            Assert.True(addRoles.Succeeded, string.Join(", ", addRoles.Errors.Select(x => x.Description)));
        }

        return user;
    }

    private sealed class AllowLoginRiskEvaluator : ILoginRiskEvaluator
    {
        public Task<LoginRiskEvaluationResult> EvaluateAsync(LoginRiskEvaluationContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new LoginRiskEvaluationResult());
    }

    private sealed class StubSignInManager : SignInManager<ApplicationUser>
    {
        private readonly SignInResult _result;

        public StubSignInManager(ApplicationUserManager userManager, SignInResult result)
            : base(
                userManager,
                new HttpContextAccessor(),
                new UserClaimsPrincipalFactory<ApplicationUser>(
                    userManager,
                    Microsoft.Extensions.Options.Options.Create(new IdentityOptions())),
                Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                NullLogger<SignInManager<ApplicationUser>>.Instance,
                new AuthenticationSchemeProvider(
                    Microsoft.Extensions.Options.Options.Create(new AuthenticationOptions())),
                new DefaultUserConfirmation<ApplicationUser>())
        {
            _result = result;
        }

        public override Task<SignInResult> CheckPasswordSignInAsync(
            ApplicationUser user,
            string password,
            bool lockoutOnFailure)
            => Task.FromResult(_result);
    }

    private sealed class NoopSessionMarkerStore : ISessionMarkerStore
    {
        public Task SetActiveAsync(Guid? tenantId, Guid userId, string sessionId, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> IsActiveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task RemoveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
