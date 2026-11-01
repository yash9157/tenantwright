using Fido2NetLib;
using Fido2NetLib.Objects;
using Identity.Application.Account;
using Identity.Application.Account.Risk;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Account.Risk;
using Identity.Application.Contracts.Caching;
using Identity.Application.Contracts.Users;
using Identity.Application.Passkeys;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.Account;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Shared;
using Shared.Caching.Abstractions;
using Shared.Caching.Sessions;
using Shared.DistributedLocking.Abstractions;
using Shared.EventContracts;
using Shared.MultiTenancy;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Identity.Tests;

public sealed class AccountAppServiceMfaTests
{
    private const string AssertionWithoutUserVerificationResponseJson =
        """
        {
          "id": "AQ",
          "rawId": "AQ",
          "type": "public-key",
          "response": {
            "authenticatorData": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAA",
            "clientDataJSON": "AQ",
            "signature": "AQ",
            "userHandle": "Ag"
          },
          "clientExtensionResults": {}
        }
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Passkey_without_signed_user_verification_should_only_require_a_configured_code_factor(
        bool configureAuthenticator)
    {
        var tenantId = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("D");
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = "passkey.mfa.user",
            NormalizedUserName = "PASSKEY.MFA.USER",
            Email = "passkey.mfa.user@example.test",
            NormalizedEmail = "PASSKEY.MFA.USER@EXAMPLE.TEST",
            EmailConfirmed = true,
            IsActive = true,
            LockoutEnabled = true,
            TwoFactorEnabled = false,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
        var createResult = await userManager.CreateAsync(user);
        Assert.True(createResult.Succeeded, string.Join(", ", createResult.Errors.Select(x => x.Description)));
        if (configureAuthenticator)
        {
            var resetKeyResult = await userManager.ResetAuthenticatorKeyAsync(user);
            Assert.True(resetKeyResult.Succeeded, string.Join(", ", resetKeyResult.Errors.Select(x => x.Description)));
            var enableResult = await userManager.SetTwoFactorEnabledAsync(user, true);
            Assert.True(enableResult.Succeeded, string.Join(", ", enableResult.Errors.Select(x => x.Description)));
        }

        var credential = new IdentityPasskeyCredential
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = user.Id,
            CredentialIdHash = Convert.ToHexString(SHA256.HashData([1])).ToLowerInvariant(),
            CredentialId = [1],
            PublicKey = [2],
            UserHandle = [2],
            SignatureCounter = 7,
            AaGuid = Guid.NewGuid(),
            FriendlyName = "Microsoft Password Manager",
            IsBackupEligible = true,
            IsBackedUp = false,
            CreatedAtUtc = DateTime.UtcNow,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
        dbContext.IdentityPasskeyCredentials.Add(credential);
        await dbContext.SaveChangesAsync();

        var challengeCache = new IdentityTestSupport.InMemoryDistributedCache<InteractiveLoginChallengeCacheItem>();
        await challengeCache.SetAsync(
            $"identity:interactive:challenge:{challengeId:D}",
            new InteractiveLoginChallengeCacheItem
            {
                ChallengeId = challengeId,
                TenantId = tenantId,
                ExpectedStep = InteractiveLoginStep.RequiresMfa,
                LoginInput = user.UserName!,
                SessionId = sessionId,
                ClientId = "tenantwright-gateway",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            });

        var ceremonyCache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var fido = new PasskeyCeremonyServiceTests.DeterministicFido2
        {
            AssertionResult = new VerifyAssertionResult
            {
                CredentialId = credential.CredentialId,
                SignCount = 8,
                IsBackedUp = true
            }
        };
        var passkeyService = new PasskeyCeremonyService(
            fido,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            ceremonyCache,
            new IdentityTestSupport.NoopDistributedLock(),
            Options.Create(new Identity.Application.Contracts.Passkeys.PasskeyOptions
            {
                Enabled = true,
                RelyingPartyId = "auth.example.test",
                RelyingPartyName = "TenantWright",
                Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "https://auth.example.test"
                },
                CeremonyTtlSeconds = 300
            }));
        var ceremony = await passkeyService.BeginAssertionAsync(user, challengeId);
        var sessionMarkerStore = new RecordingSessionMarkerStore();
        var service = CreateService(
            dbContext,
            currentTenant,
            userManager,
            challengeCache,
            new RecordingSessionRevocationService(),
            passkeyCeremonyService: passkeyService,
            sessionMarkerStore: sessionMarkerStore);

        var result = await service.CompleteInteractivePasskeyAssertionAsync(
            new Identity.Application.Contracts.Passkeys.CompleteInteractivePasskeyInput
            {
                TenantId = tenantId,
                ChallengeId = challengeId,
                CeremonyId = ceremony.CeremonyId,
                ResponseJson = AssertionWithoutUserVerificationResponseJson
            });

        Assert.Equal(
            configureAuthenticator ? InteractiveLoginStep.RequiresMfa : InteractiveLoginStep.Failed,
            result.Step);
        Assert.False(result.LoginResult.Succeeded);
        Assert.Equal(!configureAuthenticator, result.PasskeyAvailable);
        Assert.Equal(configureAuthenticator, result.AuthenticatorCodeAvailable);
        Assert.False(result.RecoveryCodeAvailable);
        Assert.NotNull(result.ChallengeId);
        Assert.Empty(await dbContext.IdentitySessions.ToListAsync());
        Assert.Equal(0, sessionMarkerStore.SetActiveCallCount);
        if (configureAuthenticator)
        {
            Assert.Equal((uint)8, credential.SignatureCounter);
            Assert.True(credential.IsBackedUp);
            Assert.NotNull(credential.LastUsedAtUtc);
            Assert.Null(challengeCache.Get($"identity:interactive:challenge:{challengeId:D}"));
            var replacementChallenge = challengeCache.Get(
                $"identity:interactive:challenge:{result.ChallengeId!.Value:D}");
            Assert.NotNull(replacementChallenge);
            Assert.Equal(InteractiveLoginStep.RequiresMfa, replacementChallenge!.ExpectedStep);
            Assert.Contains(await dbContext.IdentitySecurityLogs.ToListAsync(), x =>
                x.UserId == user.Id &&
                x.TenantId == tenantId &&
                x.Action == "PasskeyAuthenticationAdditionalFactorRequired" &&
                x.ExtraProperties != null &&
                x.ExtraProperties.Contains("user_verification_missing", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(challengeId, result.ChallengeId);
            Assert.Equal((uint)7, credential.SignatureCounter);
            Assert.False(credential.IsBackedUp);
            Assert.Null(credential.LastUsedAtUtc);
            Assert.NotNull(challengeCache.Get($"identity:interactive:challenge:{challengeId:D}"));
            Assert.Contains(await dbContext.IdentitySecurityLogs.ToListAsync(), x =>
                x.UserId == user.Id &&
                x.TenantId == tenantId &&
                x.Action == "PasskeyAuthenticationAdditionalFactorUnavailable" &&
                x.ExtraProperties != null &&
                x.ExtraProperties.Contains("user_verification_missing_no_code_factor", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task CompleteMfaAsync_should_recheck_lockout_before_accepting_valid_challenge()
    {
        var tenantId = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("D");
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = "mfa.user",
            NormalizedUserName = "MFA.USER",
            Email = "mfa.user@example.test",
            NormalizedEmail = "MFA.USER@EXAMPLE.TEST",
            EmailConfirmed = true,
            IsActive = true,
            LockoutEnabled = true,
            TwoFactorEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        var createResult = await userManager.CreateAsync(user);
        Assert.True(createResult.Succeeded, string.Join(", ", createResult.Errors.Select(x => x.Description)));

        var recoveryCodes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 1))!.ToList();
        var recoveryCode = Assert.Single(recoveryCodes);
        var lockResult = await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.True(lockResult.Succeeded, string.Join(", ", lockResult.Errors.Select(x => x.Description)));

        var challengeCache = new IdentityTestSupport.InMemoryDistributedCache<InteractiveLoginChallengeCacheItem>();
        await challengeCache.SetAsync(
            $"identity:interactive:challenge:{challengeId:D}",
            new InteractiveLoginChallengeCacheItem
            {
                ChallengeId = challengeId,
                TenantId = tenantId,
                ExpectedStep = InteractiveLoginStep.RequiresMfa,
                LoginInput = user.UserName!,
                SessionId = sessionId,
                ClientId = "tenantwright-gateway",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            });

        var revocationService = new RecordingSessionRevocationService();
        var service = CreateService(dbContext, currentTenant, userManager, challengeCache, revocationService);

        var result = await service.CompleteMfaAsync(new CompleteMfaInput
        {
            TenantId = tenantId,
            ChallengeId = challengeId,
            TwoFactorRecoveryCode = recoveryCode
        });

        Assert.Equal(InteractiveLoginStep.Locked, result.Step);
        Assert.False(result.LoginResult.Succeeded);
        Assert.True(result.LoginResult.IsLockedOut);
        Assert.Empty(await dbContext.IdentitySessions.ToListAsync());
        Assert.Equal((user.Id, tenantId, "UserLockedOut"), revocationService.LastCall);
        Assert.Contains(await dbContext.IdentitySecurityLogs.ToListAsync(), x =>
            x.UserId == user.Id &&
            x.TenantId == tenantId &&
            x.Action == "TwoFactorLockedOut");
    }

    [Fact]
    public async Task CompleteMfaAsync_should_not_reactivate_revoked_session_rows()
    {
        var tenantId = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("D");
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = "revoked.mfa.user",
            NormalizedUserName = "REVOKED.MFA.USER",
            Email = "revoked.mfa.user@example.test",
            NormalizedEmail = "REVOKED.MFA.USER@EXAMPLE.TEST",
            EmailConfirmed = true,
            IsActive = true,
            LockoutEnabled = true,
            TwoFactorEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        var createResult = await userManager.CreateAsync(user);
        Assert.True(createResult.Succeeded, string.Join(", ", createResult.Errors.Select(x => x.Description)));

        var recoveryCodes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 1))!.ToList();
        var recoveryCode = Assert.Single(recoveryCodes);
        var revokedAtUtc = DateTime.UtcNow.AddMinutes(-5);
        dbContext.IdentitySessions.Add(new Identity.Domain.ApplicationUsers.IdentitySession
        {
            Id = Guid.Parse(sessionId),
            TenantId = tenantId,
            UserId = user.Id,
            SessionId = sessionId,
            CreationTime = revokedAtUtc.AddMinutes(-10),
            LastActiveTime = revokedAtUtc.AddMinutes(-1),
            Status = Identity.Domain.Shared.ApplicationUsers.IdentitySessionStatus.Revoked,
            RevokedAtUtc = revokedAtUtc,
            RevocationSource = "Logout"
        });
        await dbContext.SaveChangesAsync();

        var challengeCache = new IdentityTestSupport.InMemoryDistributedCache<InteractiveLoginChallengeCacheItem>();
        await challengeCache.SetAsync(
            $"identity:interactive:challenge:{challengeId:D}",
            new InteractiveLoginChallengeCacheItem
            {
                ChallengeId = challengeId,
                TenantId = tenantId,
                ExpectedStep = InteractiveLoginStep.RequiresMfa,
                LoginInput = user.UserName!,
                SessionId = sessionId,
                ClientId = "tenantwright-gateway",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            });

        var service = CreateService(
            dbContext,
            currentTenant,
            userManager,
            challengeCache,
            new RecordingSessionRevocationService());

        await Assert.ThrowsAsync<SharedValidationException>(() => service.CompleteMfaAsync(new CompleteMfaInput
        {
            TenantId = tenantId,
            ChallengeId = challengeId,
            TwoFactorRecoveryCode = recoveryCode
        }));

        var session = await dbContext.IdentitySessions.SingleAsync(x => x.SessionId == sessionId);
        Assert.Equal(Identity.Domain.Shared.ApplicationUsers.IdentitySessionStatus.Revoked, session.Status);
        Assert.Equal(revokedAtUtc, session.RevokedAtUtc);
        Assert.Equal("Logout", session.RevocationSource);
    }

    [Fact]
    public async Task ConfirmEmailVerificationAsync_should_clear_activation_block_and_revoke_sessions()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = "verify.email.user",
            NormalizedUserName = "VERIFY.EMAIL.USER",
            Email = "verify.email.user@example.test",
            NormalizedEmail = "VERIFY.EMAIL.USER@EXAMPLE.TEST",
            EmailConfirmed = false,
            RequireEmailActivationBeforeLogin = true,
            IsActive = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        var createResult = await userManager.CreateAsync(user);
        Assert.True(createResult.Succeeded, string.Join(", ", createResult.Errors.Select(x => x.Description)));
        var token = EncodeWireToken(await userManager.GenerateEmailConfirmationTokenAsync(user));
        var revocationService = new RecordingSessionRevocationService();
        var service = CreateService(
            dbContext,
            currentTenant,
            userManager,
            new IdentityTestSupport.InMemoryDistributedCache<InteractiveLoginChallengeCacheItem>(),
            revocationService);

        var result = await service.ConfirmEmailVerificationAsync(new EmailVerificationConfirmInput
        {
            TenantId = tenantId,
            UserId = user.Id,
            Token = token
        });

        var row = await dbContext.Users.SingleAsync(x => x.Id == user.Id);
        Assert.True(result.Succeeded);
        Assert.True(row.EmailConfirmed);
        Assert.False(row.RequireEmailActivationBeforeLogin);
        Assert.Equal((user.Id, tenantId, "EmailVerified"), revocationService.LastCall);
    }

    [Fact]
    public async Task CompletePasswordResetAsync_should_clear_activation_block_and_revoke_sessions()
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = "reset.activation.user",
            NormalizedUserName = "RESET.ACTIVATION.USER",
            Email = "reset.activation.user@example.test",
            NormalizedEmail = "RESET.ACTIVATION.USER@EXAMPLE.TEST",
            EmailConfirmed = false,
            RequireEmailActivationBeforeLogin = true,
            ShouldChangePasswordOnNextLogin = true,
            IsActive = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        var createResult = await userManager.CreateAsync(user, "Initial#12345");
        Assert.True(createResult.Succeeded, string.Join(", ", createResult.Errors.Select(x => x.Description)));
        var token = EncodeWireToken(await userManager.GeneratePasswordResetTokenAsync(user));
        var revocationService = new RecordingSessionRevocationService();
        var service = CreateService(
            dbContext,
            currentTenant,
            userManager,
            new IdentityTestSupport.InMemoryDistributedCache<InteractiveLoginChallengeCacheItem>(),
            revocationService);

        var result = await service.CompletePasswordResetAsync(new PasswordResetCompleteInput
        {
            TenantId = tenantId,
            UserId = user.Id,
            Token = token,
            NewPassword = "Reset#12345",
            ConfirmPassword = "Reset#12345"
        });

        var row = await dbContext.Users.SingleAsync(x => x.Id == user.Id);
        Assert.True(result.Succeeded);
        Assert.True(row.EmailConfirmed);
        Assert.False(row.RequireEmailActivationBeforeLogin);
        Assert.False(row.ShouldChangePasswordOnNextLogin);
        Assert.Equal((user.Id, tenantId, "PasswordReset"), revocationService.LastCall);
    }

    [Fact]
    public async Task AcceptPublicInvitationAsync_should_claim_once_persist_outbox_and_ignore_post_commit_cache_failure()
    {
        var tenantId = Guid.NewGuid();
        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var currentTenant = new IdentityTestSupport.TestCurrentTenant(tenantId, "tenant-a");
        var messageSender = new IdentityTestSupport.CapturingMessageSender();

        await using var dbContext = IdentityTestSupport.CreateDbContext(currentTenant);
        var userManager = IdentityTestSupport.CreateUserManager(dbContext, currentTenant);
        var roleManager = IdentityTestSupport.CreateRoleManager(dbContext, currentTenant);
        var roleResult = await roleManager.CreateAsync(new ApplicationRole(TenantWrightIdentityRoleNames.TenantAdmin)
        {
            TenantId = tenantId
        });
        Assert.True(roleResult.Succeeded, string.Join(", ", roleResult.Errors.Select(x => x.Description)));

        dbContext.IdentityPublicInvitations.Add(new IdentityPublicInvitation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TenantName = "tenant-a",
            Email = "invitee@example.test",
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant(),
            RoleName = TenantWrightIdentityRoleNames.TenantAdmin,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        await dbContext.SaveChangesAsync();

        var service = CreateService(
            dbContext,
            currentTenant,
            userManager,
            new IdentityTestSupport.InMemoryDistributedCache<InteractiveLoginChallengeCacheItem>(),
            new RecordingSessionRevocationService(),
            new EmailVerificationOptions
            {
                TemplateName = "Identity.EmailVerificationLink",
                AllowedBaseUrls = ["https://auth.example.test"]
            },
            messageSender: messageSender,
            cacheInvalidator: new ThrowingIdentityAuthCacheInvalidator());

        var input = new PublicInvitationAcceptInput
        {
            Token = rawToken,
            Email = "invitee@example.test",
            UserName = "invitee",
            Password = "Invite#12345",
            ConfirmPassword = "Invite#12345",
            VerificationBaseUrl = "https://auth.example.test"
        };

        var first = await service.AcceptPublicInvitationAsync(input);
        var second = await service.AcceptPublicInvitationAsync(input);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.True(second.IsUsed);
        var user = Assert.Single(await dbContext.Users.Where(x => x.TenantId == tenantId).ToListAsync());
        Assert.True(await userManager.IsInRoleAsync(user, TenantWrightIdentityRoleNames.TenantAdmin));
        var invitation = await dbContext.IdentityPublicInvitations.SingleAsync();
        Assert.Equal(user.Id, invitation.AcceptedUserId);
        Assert.NotNull(invitation.AcceptedAtUtc);
        var outgoing = Assert.Single(messageSender.Published);
        var notification = Assert.IsType<NotificationSendRequestedTenantEto>(outgoing.EventData);
        Assert.Equal(tenantId, notification.TenantId);
    }

    private static AccountAppService CreateService(
        IdentityDbContextApp dbContext,
        ICurrentTenant currentTenant,
        ApplicationUserManager userManager,
        IDistributedCache<InteractiveLoginChallengeCacheItem> challengeCache,
        IIdentitySessionRevocationService sessionRevocationService,
        EmailVerificationOptions? emailVerificationOptions = null,
        PasskeyCeremonyService? passkeyCeremonyService = null,
        ISessionMarkerStore? sessionMarkerStore = null,
        IdentityTestSupport.CapturingMessageSender? messageSender = null,
        IIdentityAuthCacheInvalidator? cacheInvalidator = null)
    {
        var notificationPublisher = messageSender ?? new IdentityTestSupport.CapturingMessageSender();
        return new AccountAppService(
            userManager,
            signInManager: null!,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            new IdentityTestSupport.InMemoryDistributedCache<IdentitySessionValidityCacheItem>(),
            cacheInvalidator ?? new IdentityTestSupport.NoopIdentityAuthCacheInvalidator(),
            sessionMarkerStore ?? new NoopSessionMarkerStore(),
            Options.Create(new SessionValidationOptions()),
            sessionRevocationService,
            new AllowLoginRiskEvaluator(),
            Options.Create(new LoginRiskOptions()),
            Options.Create(new LoginRiskAlertingOptions()),
            Options.Create(new PasswordResetOptions()),
            Options.Create(emailVerificationOptions ?? new EmailVerificationOptions()),
            Options.Create(new InteractiveLoginOptions()),
            Options.Create(new LoginAttemptProtectionOptions()),
            new IdentityUserSecurityAlertPublisher(notificationPublisher, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityUserSecurityAlertPublisher>.Instance),
            new IdentityTestSupport.InMemoryDistributedCache<AccountAppService.PasswordResetQuotaState>(),
            new IdentityTestSupport.InMemoryDistributedCache<AccountAppService.LoginAttemptQuotaState>(),
            challengeCache,
            new IdentityTestSupport.NoopDistributedLock(),
            IdentityTestSupport.CreateRoleManager(dbContext, currentTenant),
            currentTenant,
            eventBus: notificationPublisher,
            passkeyCeremonyService: passkeyCeremonyService);
    }

    private static string EncodeWireToken(string token)
    {
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
    }

    private sealed class AllowLoginRiskEvaluator : ILoginRiskEvaluator
    {
        public Task<LoginRiskEvaluationResult> EvaluateAsync(LoginRiskEvaluationContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new LoginRiskEvaluationResult());
    }

    private sealed class RecordingSessionRevocationService : IIdentitySessionRevocationService
    {
        public (Guid UserId, Guid? TenantId, string Action)? LastCall { get; private set; }

        public Task<int> RevokeAllAsync(Guid userId, Guid? tenantId, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
        {
            LastCall = (userId, tenantId, action);
            return Task.FromResult(0);
        }

        public Task<int> RevokeAllByTenantAsync(Guid tenantId, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<BulkRevokeSessionsResultDto> RevokeSelectedAsync(IReadOnlyCollection<Guid> sessionIds, string action, string? revocationSource = null, Guid? revokedByUserId = null, string? revocationReason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BulkRevokeSessionsResultDto());
    }

    private sealed class ThrowingIdentityAuthCacheInvalidator : IIdentityAuthCacheInvalidator
    {
        private readonly InvalidOperationException _failure = new("simulated cache outage");

        public Task InvalidateAliasesAsync(Guid? tenantId, IEnumerable<string> aliasHashes, CancellationToken cancellationToken = default)
            => FailAsync();

        public Task InvalidateAuthorizationAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default)
            => FailAsync();

        public Task InvalidateProfileAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default)
            => FailAsync();

        public Task InvalidateSessionsAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default)
            => FailAsync();

        private Task FailAsync() => Task.FromException(_failure);
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

    private sealed class RecordingSessionMarkerStore : ISessionMarkerStore
    {
        public int SetActiveCallCount { get; private set; }

        public Task SetActiveAsync(Guid? tenantId, Guid userId, string sessionId, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            SetActiveCallCount++;
            return Task.CompletedTask;
        }

        public Task<string?> IsActiveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task RemoveAsync(Guid? tenantId, Guid userId, string sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
