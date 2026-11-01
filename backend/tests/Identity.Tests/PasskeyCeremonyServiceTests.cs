using Fido2NetLib;
using Fido2NetLib.Exceptions;
using Fido2NetLib.Objects;
using Identity.Application.Account;
using Identity.Application.Contracts.Passkeys;
using Identity.Application.Passkeys;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.Account;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared;
using Xunit;

namespace Identity.Tests;

public sealed class PasskeyCeremonyServiceTests
{
    private const string AttestationResponseJson =
        """
        {
          "id": "AQ",
          "rawId": "AQ",
          "type": "public-key",
          "response": {
            "attestationObject": "AQ",
            "clientDataJSON": "AQ",
            "transports": []
          },
          "clientExtensionResults": {}
        }
        """;

    private const string AssertionResponseJson =
        """
        {
          "id": "AQ",
          "rawId": "AQ",
          "type": "public-key",
          "response": {
            "authenticatorData": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFAAAAAA",
            "clientDataJSON": "AQ",
            "signature": "AQ",
            "userHandle": "Ag"
          },
          "clientExtensionResults": {}
        }
        """;

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

    [Fact]
    public async Task Verified_registration_should_remain_untracked_until_caller_accepts_login_challenge()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var cache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var fido = new DeterministicFido2();
        var service = CreateService(dbContext, cache, fido);
        var user = CreateUser();

        var ceremony = await service.BeginRegistrationAsync(
            user,
            "Security key",
            PasskeyCeremonyPurpose.InteractiveEnrollment,
            Guid.NewGuid());
        var credential = await service.CompleteRegistrationAsync(
            user,
            ceremony.CeremonyId,
            AttestationResponseJson,
            PasskeyCeremonyPurpose.InteractiveEnrollment,
            ExtractInteractiveChallengeId(cache, ceremony.CeremonyId));

        Assert.Empty(dbContext.IdentityPasskeyCredentials.Local);
        Assert.Equal(user.Id, credential.UserId);
        Assert.Equal(user.TenantId, credential.TenantId);
    }

    [Fact]
    public async Task Real_fido_options_should_bind_rp_and_require_user_verification()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var cache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var fido = new Fido2(new Fido2Configuration
        {
            ServerDomain = "auth.example.test",
            ServerName = "TenantWright",
            Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "https://auth.example.test"
            }
        });
        var service = CreateService(dbContext, cache, fido);
        var user = CreateUser();

        var registration = await service.BeginRegistrationAsync(
            user,
            "Security key",
            PasskeyCeremonyPurpose.InteractiveEnrollment,
            Guid.NewGuid());
        var registrationOptions = CredentialCreateOptions.FromJson(registration.OptionsJson);

        Assert.Equal("auth.example.test", registrationOptions.Rp.Id);
        Assert.Equal(
            UserVerificationRequirement.Required,
            registrationOptions.AuthenticatorSelection.UserVerification);

        var credential = CreateCredential(user, signatureCounter: 0);
        await dbContext.IdentityPasskeyCredentials.AddAsync(credential);
        await dbContext.SaveChangesAsync();
        var assertion = await service.BeginAssertionAsync(user, Guid.NewGuid());
        var assertionOptions = AssertionOptions.FromJson(assertion.OptionsJson);

        Assert.Equal("auth.example.test", assertionOptions.RpId);
        Assert.Equal(UserVerificationRequirement.Preferred, assertionOptions.UserVerification);
        Assert.Equal(credential.CredentialId, Assert.Single(assertionOptions.AllowCredentials).Id);
    }

    [Fact]
    public async Task Registration_ceremony_should_be_single_use()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var cache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var service = CreateService(dbContext, cache, new DeterministicFido2());
        var user = CreateUser();
        var interactiveChallengeId = Guid.NewGuid();
        var ceremony = await service.BeginRegistrationAsync(
            user,
            "Security key",
            PasskeyCeremonyPurpose.InteractiveEnrollment,
            interactiveChallengeId);

        await service.CompleteRegistrationAsync(
            user,
            ceremony.CeremonyId,
            AttestationResponseJson,
            PasskeyCeremonyPurpose.InteractiveEnrollment,
            interactiveChallengeId);

        await Assert.ThrowsAsync<SharedValidationException>(() =>
            service.CompleteRegistrationAsync(
                user,
                ceremony.CeremonyId,
                AttestationResponseJson,
                PasskeyCeremonyPurpose.InteractiveEnrollment,
                interactiveChallengeId));
    }

    [Fact]
    public async Task Registration_ceremony_should_reject_wrong_tenant()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var cache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var service = CreateService(dbContext, cache, new DeterministicFido2());
        var user = CreateUser();
        var interactiveChallengeId = Guid.NewGuid();
        var ceremony = await service.BeginRegistrationAsync(
            user,
            "Security key",
            PasskeyCeremonyPurpose.InteractiveEnrollment,
            interactiveChallengeId);
        var wrongTenantUser = CreateUser(user.Id, Guid.NewGuid());

        await Assert.ThrowsAsync<SharedValidationException>(() =>
            service.CompleteRegistrationAsync(
                wrongTenantUser,
                ceremony.CeremonyId,
                AttestationResponseJson,
                PasskeyCeremonyPurpose.InteractiveEnrollment,
                interactiveChallengeId));
    }

    [Fact]
    public async Task Registration_ceremony_should_reject_wrong_purpose()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var cache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var service = CreateService(dbContext, cache, new DeterministicFido2());
        var user = CreateUser();
        var ceremony = await service.BeginRegistrationAsync(
            user,
            "Security key",
            PasskeyCeremonyPurpose.InteractiveEnrollment,
            Guid.NewGuid());

        await Assert.ThrowsAsync<SharedValidationException>(() =>
            service.CompleteRegistrationAsync(
                user,
                ceremony.CeremonyId,
                AttestationResponseJson,
                PasskeyCeremonyPurpose.SelfServiceRegistration,
                interactiveChallengeId: null));
    }

    [Fact]
    public async Task Registration_ceremony_should_reject_expired_state()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var cache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var service = CreateService(dbContext, cache, new DeterministicFido2());
        var user = CreateUser();
        var interactiveChallengeId = Guid.NewGuid();
        var ceremony = await service.BeginRegistrationAsync(
            user,
            "Security key",
            PasskeyCeremonyPurpose.InteractiveEnrollment,
            interactiveChallengeId);
        cache.Set(
            BuildCacheKey(ceremony.CeremonyId),
            new PasskeyCeremonyCacheItem
            {
                CeremonyId = ceremony.CeremonyId,
                Purpose = PasskeyCeremonyPurpose.InteractiveEnrollment,
                UserId = user.Id,
                TenantId = user.TenantId,
                InteractiveChallengeId = interactiveChallengeId,
                FriendlyName = "Security key",
                OriginalOptionsJson = ceremony.OptionsJson,
                ConfigurationFingerprint = "expired",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
            });

        await Assert.ThrowsAsync<SharedValidationException>(() =>
            service.CompleteRegistrationAsync(
                user,
                ceremony.CeremonyId,
                AttestationResponseJson,
                PasskeyCeremonyPurpose.InteractiveEnrollment,
                interactiveChallengeId));
    }

    [Fact]
    public async Task Fido_verification_failure_should_fail_closed_without_tracking_credential()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var cache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var service = CreateService(
            dbContext,
            cache,
            new DeterministicFido2 { RegistrationException = new Fido2VerificationException("Unexpected origin.") });
        var user = CreateUser();
        var interactiveChallengeId = Guid.NewGuid();
        var ceremony = await service.BeginRegistrationAsync(
            user,
            "Security key",
            PasskeyCeremonyPurpose.InteractiveEnrollment,
            interactiveChallengeId);

        var exception = await Assert.ThrowsAsync<PasskeyVerificationException>(() =>
            service.CompleteRegistrationAsync(
                user,
                ceremony.CeremonyId,
                AttestationResponseJson,
                PasskeyCeremonyPurpose.InteractiveEnrollment,
                interactiveChallengeId));
        Assert.Equal("fido_Unknown", exception.Outcome);
        Assert.Empty(dbContext.IdentityPasskeyCredentials.Local);
    }

    [Fact]
    public async Task Assertion_verification_failure_should_preserve_bounded_fido_error_code()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var user = CreateUser();
        var credential = CreateCredential(user, signatureCounter: 0);
        await dbContext.IdentityPasskeyCredentials.AddAsync(credential);
        await dbContext.SaveChangesAsync();
        var cache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var service = CreateService(
            dbContext,
            cache,
            new DeterministicFido2
            {
                AssertionException = new Fido2VerificationException(
                    Fido2ErrorCode.InvalidSignature,
                    "Signature rejected.")
            });
        var interactiveChallengeId = Guid.NewGuid();
        var ceremony = await service.BeginAssertionAsync(user, interactiveChallengeId);

        var exception = await Assert.ThrowsAsync<PasskeyVerificationException>(() =>
            service.CompleteAssertionAsync(
                user,
                ceremony.CeremonyId,
                AssertionResponseJson,
                interactiveChallengeId));

        Assert.Equal("fido_InvalidSignature", exception.Outcome);
        Assert.Null(credential.LastUsedAtUtc);
    }

    [Fact]
    public void Verification_failure_result_should_not_claim_login_challenge_expired()
    {
        var result = AccountAppService.PasskeyVerificationFailed();

        Assert.Equal(InteractiveLoginStep.Failed, result.Step);
        Assert.Equal("Passkey verification failed. Start sign-in again.", result.Message);
    }

    [Fact]
    public async Task Assertion_counter_regression_should_fail_closed_without_mutating_credential()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var user = CreateUser();
        var credential = CreateCredential(user, signatureCounter: 7);
        await dbContext.IdentityPasskeyCredentials.AddAsync(credential);
        await dbContext.SaveChangesAsync();
        var cache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var service = CreateService(
            dbContext,
            cache,
            new DeterministicFido2
            {
                AssertionResult = new VerifyAssertionResult
                {
                    CredentialId = credential.CredentialId,
                    SignCount = 7,
                    IsBackedUp = true
                }
            });
        var interactiveChallengeId = Guid.NewGuid();
        var ceremony = await service.BeginAssertionAsync(user, interactiveChallengeId);

        await Assert.ThrowsAsync<SharedValidationException>(() =>
            service.CompleteAssertionAsync(
                user,
                ceremony.CeremonyId,
                AssertionResponseJson,
                interactiveChallengeId));
        Assert.Equal((uint)7, credential.SignatureCounter);
        Assert.Null(credential.LastUsedAtUtc);
    }

    [Fact]
    public async Task Verified_assertion_should_not_mutate_credential_until_applied_by_caller()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var user = CreateUser();
        var credential = CreateCredential(user, signatureCounter: 7);
        await dbContext.IdentityPasskeyCredentials.AddAsync(credential);
        await dbContext.SaveChangesAsync();
        var cache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var service = CreateService(
            dbContext,
            cache,
            new DeterministicFido2
            {
                AssertionResult = new VerifyAssertionResult
                {
                    CredentialId = credential.CredentialId,
                    SignCount = 8,
                    IsBackedUp = true
                }
            });
        var interactiveChallengeId = Guid.NewGuid();
        var ceremony = await service.BeginAssertionAsync(user, interactiveChallengeId);

        var verification = await service.CompleteAssertionAsync(
            user,
            ceremony.CeremonyId,
            AssertionResponseJson,
            interactiveChallengeId);

        Assert.True(verification.UserVerified);
        Assert.Equal((uint)7, credential.SignatureCounter);
        Assert.Null(credential.LastUsedAtUtc);

        verification.Apply();

        Assert.Equal((uint)8, credential.SignatureCounter);
        Assert.True(credential.IsBackedUp);
        Assert.NotNull(credential.LastUsedAtUtc);
    }

    [Fact]
    public async Task Verified_assertion_should_report_missing_signed_user_verification_flag()
    {
        await using var dbContext = IdentityTestSupport.CreateDbContext();
        var user = CreateUser();
        var credential = CreateCredential(user, signatureCounter: 7);
        await dbContext.IdentityPasskeyCredentials.AddAsync(credential);
        await dbContext.SaveChangesAsync();
        var cache = new IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem>();
        var service = CreateService(
            dbContext,
            cache,
            new DeterministicFido2
            {
                AssertionResult = new VerifyAssertionResult
                {
                    CredentialId = credential.CredentialId,
                    SignCount = 8,
                    IsBackedUp = true
                }
            });
        var interactiveChallengeId = Guid.NewGuid();
        var ceremony = await service.BeginAssertionAsync(user, interactiveChallengeId);

        var verification = await service.CompleteAssertionAsync(
            user,
            ceremony.CeremonyId,
            AssertionWithoutUserVerificationResponseJson,
            interactiveChallengeId);

        Assert.False(verification.UserVerified);
        Assert.Equal((uint)7, credential.SignatureCounter);
        Assert.Null(credential.LastUsedAtUtc);
    }

    private static PasskeyCeremonyService CreateService(
        IdentityDbContextApp dbContext,
        IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem> cache,
        IFido2 fido)
    {
        return new PasskeyCeremonyService(
            fido,
            IdentityTestSupport.AsDbContextProvider(dbContext),
            cache,
            new IdentityTestSupport.NoopDistributedLock(),
            Options.Create(new PasskeyOptions
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
    }

    private static ApplicationUser CreateUser(Guid? userId = null, Guid? tenantId = null)
    {
        var id = userId ?? Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = id,
            TenantId = tenantId ?? Guid.NewGuid(),
            UserName = $"user-{id:N}",
            Name = "Passkey Test User"
        };
        _ = new IdentityTestSupport.TestCurrentTenant(user.TenantId);
        return user;
    }

    private static IdentityPasskeyCredential CreateCredential(
        ApplicationUser user,
        uint signatureCounter)
    {
        return new IdentityPasskeyCredential
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            UserId = user.Id,
            CredentialIdHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData([1])).ToLowerInvariant(),
            CredentialId = [1],
            PublicKey = [2],
            UserHandle = [2],
            SignatureCounter = signatureCounter,
            AaGuid = Guid.NewGuid(),
            FriendlyName = "Security key",
            IsBackupEligible = true,
            IsBackedUp = false,
            CreatedAtUtc = DateTime.UtcNow,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
    }

    private static Guid ExtractInteractiveChallengeId(
        IdentityTestSupport.InMemoryDistributedCache<PasskeyCeremonyCacheItem> cache,
        Guid ceremonyId)
    {
        return cache.Get(BuildCacheKey(ceremonyId))!.InteractiveChallengeId!.Value;
    }

    private static string BuildCacheKey(Guid ceremonyId)
        => $"identity:passkey:ceremony:{ceremonyId:D}";

    internal sealed class DeterministicFido2 : IFido2
    {
        public Exception? RegistrationException { get; init; }

        public Exception? AssertionException { get; init; }

        public VerifyAssertionResult AssertionResult { get; init; } = new()
        {
            CredentialId = [1],
            SignCount = 1,
            IsBackedUp = false
        };

        public CredentialCreateOptions RequestNewCredential(
            RequestNewCredentialParams request)
        {
            return new CredentialCreateOptions
            {
                Rp = new PublicKeyCredentialRpEntity("auth.example.test", "TenantWright"),
                User = request.User,
                Challenge = [3],
                PubKeyCredParams = [PubKeyCredParam.ES256],
                AuthenticatorSelection = request.AuthenticatorSelection,
                Attestation = request.AttestationPreference,
                ExcludeCredentials = request.ExcludeCredentials
            };
        }

        public Task<RegisteredPublicKeyCredential> MakeNewCredentialAsync(
            MakeNewCredentialParams request,
            CancellationToken cancellationToken = default)
        {
            if (RegistrationException != null)
            {
                return Task.FromException<RegisteredPublicKeyCredential>(RegistrationException);
            }

            return Task.FromResult(new RegisteredPublicKeyCredential
            {
                Id = [1],
                PublicKey = [2],
                Transports = [],
                SignCount = 0,
                IsBackupEligible = true,
                IsBackedUp = false,
                AaGuid = Guid.NewGuid(),
                User = new Fido2User
                {
                    Id = [2],
                    Name = "test-user",
                    DisplayName = "Test User"
                },
                AttestationFormat = "none",
                AttestationObject = [],
                AttestationClientDataJson = []
            });
        }

        public AssertionOptions GetAssertionOptions(GetAssertionOptionsParams request)
        {
            return new AssertionOptions
            {
                Challenge = [4],
                RpId = "auth.example.test",
                AllowCredentials = request.AllowedCredentials,
                UserVerification = request.UserVerification
            };
        }

        public Task<VerifyAssertionResult> MakeAssertionAsync(
            MakeAssertionParams request,
            CancellationToken cancellationToken = default)
        {
            if (AssertionException != null)
            {
                return Task.FromException<VerifyAssertionResult>(AssertionException);
            }

            return Task.FromResult(AssertionResult);
        }
    }
}
