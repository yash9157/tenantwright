using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Identity.Application.Contracts.Passkeys;
using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Shared;
using Shared.Caching.Abstractions;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;
using Shared.Modularity.DependencyInjection;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Passkeys;

public sealed class PasskeyCeremonyService : ITransientDependency
{
    private const int AuthenticatorDataMinimumLength = 37;
    private const int AuthenticatorFlagsOffset = 32;
    private const int MaximumCredentialsPerUser = 10;
    private const byte UserVerifiedFlag = 0x04;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly DistributedLockAcquireOptions CeremonyLockOptions = new()
    {
        PolicyName = DistributedLockPolicyNames.ApiIdempotency,
        AcquireTimeoutMs = 500,
        LeaseMs = 15000,
        FailureMode = DistributedLockFailureMode.FailClosed,
        IgnoreTenant = true
    };

    private readonly IFido2 _fido2;
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly IDistributedCache<PasskeyCeremonyCacheItem> _ceremonyCache;
    private readonly IDistributedLock _distributedLock;
    private readonly IOptions<PasskeyOptions> _options;

    public PasskeyCeremonyService(
        IFido2 fido2, IDbContextProvider<IdentityDbContextApp> dbContextProvider, IDistributedCache<PasskeyCeremonyCacheItem> ceremonyCache, IDistributedLock distributedLock, IOptions<PasskeyOptions> options)
    {
        _fido2 = fido2;
        _dbContextProvider = dbContextProvider;
        _ceremonyCache = ceremonyCache;
        _distributedLock = distributedLock;
        _options = options;
    }

    public async Task<PasskeyCeremonyOptionsDto> BeginRegistrationAsync(
        ApplicationUser user,
        string friendlyName,
        PasskeyCeremonyPurpose purpose,
        Guid? interactiveChallengeId,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var normalizedName = NormalizeFriendlyName(friendlyName);
        var existing = (await ListUserCredentialsAsync(user.Id, NormalizeTenantId(user), cancellationToken))
            .OrderBy(x => x.CreatedAtUtc)
            .ToList();
        if (existing.Count >= MaximumCredentialsPerUser)
        {
            PasskeyMetrics.Record(RegistrationOperation(purpose), "capacity_rejected");
            throw new SharedValidationException($"No more than {MaximumCredentialsPerUser} passkeys may be registered.");
        }

        var fidoUser = CreateFidoUser(user);
        var descriptors = existing.Select(ToDescriptor).ToList();
        var authenticatorSelection = new AuthenticatorSelection
        {
            ResidentKey = ResidentKeyRequirement.Preferred,
            UserVerification = UserVerificationRequirement.Required
        };
        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fidoUser,
            ExcludeCredentials = descriptors,
            AuthenticatorSelection = authenticatorSelection,
            AttestationPreference = AttestationConveyancePreference.None
        });

        return await StoreCeremonyAsync(
            user,
            normalizedName,
            purpose,
            interactiveChallengeId,
            options.ToJson(),
            cancellationToken);
    }

    public async Task<IdentityPasskeyCredential> CompleteRegistrationAsync(
        ApplicationUser user,
        Guid ceremonyId,
        string responseJson,
        PasskeyCeremonyPurpose purpose,
        Guid? interactiveChallengeId,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureEnabled();
        var ceremony = await ConsumeCeremonyAsync(
            ceremonyId,
            user,
            purpose,
            interactiveChallengeId,
            cancellationToken);
        AuthenticatorAttestationRawResponse response;
        try
        {
            response = Deserialize<AuthenticatorAttestationRawResponse>(responseJson);
        }
        catch (SharedValidationException)
        {
            PasskeyMetrics.Record(RegistrationOperation(purpose), "malformed_response");
            throw;
        }

        var originalOptions = CredentialCreateOptions.FromJson(ceremony.OriginalOptionsJson);

        try
        {
            var credential = await _fido2.MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = response,
                    OriginalOptions = originalOptions,
                    IsCredentialIdUniqueToUserCallback = async (args, token) =>
                    {
                        var hash = ComputeCredentialIdHash(args.CredentialId);
                        return !await unitOfWorkDbContext.IdentityPasskeyCredentials
                            .AsNoTracking()
                            .AnyAsync(x => x.CredentialIdHash == hash, token);
                    }
                },
                cancellationToken: cancellationToken);

            var entity = new IdentityPasskeyCredential
            {
                Id = Guid.CreateVersion7(),
                TenantId = NormalizeTenantId(user),
                UserId = user.Id,
                CredentialIdHash = ComputeCredentialIdHash(credential.Id),
                CredentialId = credential.Id,
                PublicKey = credential.PublicKey,
                UserHandle = credential.User.Id,
                SignatureCounter = credential.SignCount,
                AaGuid = credential.AaGuid,
                Transports = SerializeTransports(credential.Transports),
                FriendlyName = ceremony.FriendlyName,
                IsBackupEligible = credential.IsBackupEligible,
                IsBackedUp = credential.IsBackedUp,
                CreatedAtUtc = DateTime.UtcNow,
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            };

            return entity;
        }
        catch (Fido2VerificationException exception)
        {
            var outcome = PasskeyVerificationException.BuildOutcome(exception.Code);
            PasskeyMetrics.Record(RegistrationOperation(purpose), outcome);
            throw new PasskeyVerificationException(
                "Passkey registration verification failed.",
                outcome);
        }
    }

    public async Task<PasskeyCeremonyOptionsDto> BeginAssertionAsync(
        ApplicationUser user,
        Guid interactiveChallengeId,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var credentials = (await ListUserCredentialsAsync(user.Id, NormalizeTenantId(user), cancellationToken))
            .OrderBy(x => x.CreatedAtUtc)
            .ToList();
        if (credentials.Count == 0)
        {
            PasskeyMetrics.Record("interactive_assertion", "credential_missing");
            throw new SharedValidationException("No passkey is registered for this account.");
        }

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials.Select(ToDescriptor).ToList(),
            UserVerification = UserVerificationRequirement.Preferred
        });

        return await StoreCeremonyAsync(
            user,
            friendlyName: string.Empty,
            PasskeyCeremonyPurpose.InteractiveAssertion,
            interactiveChallengeId,
            options.ToJson(),
            cancellationToken);
    }

    internal async Task<VerifiedPasskeyAssertion> CompleteAssertionAsync(
        ApplicationUser user,
        Guid ceremonyId,
        string responseJson,
        Guid interactiveChallengeId,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var ceremony = await ConsumeCeremonyAsync(
            ceremonyId,
            user,
            PasskeyCeremonyPurpose.InteractiveAssertion,
            interactiveChallengeId,
            cancellationToken);
        AuthenticatorAssertionRawResponse response;
        try
        {
            response = Deserialize<AuthenticatorAssertionRawResponse>(responseJson);
        }
        catch (SharedValidationException)
        {
            PasskeyMetrics.Record("interactive_assertion", "malformed_response");
            throw;
        }

        var credentialHash = ComputeCredentialIdHash(response.RawId);
        var tenantId = NormalizeTenantId(user);
        var credential = await FindUserCredentialByHashAsync(user.Id, tenantId, credentialHash, cancellationToken)
            ?? throw PasskeyAssertionRejected("credential_mismatch");
        var originalOptions = AssertionOptions.FromJson(ceremony.OriginalOptionsJson);

        try
        {
            var result = await _fido2.MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = response,
                    OriginalOptions = originalOptions,
                    StoredPublicKey = credential.PublicKey,
                    StoredSignatureCounter = credential.SignatureCounter,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                        args.CredentialId.AsSpan().SequenceEqual(credential.CredentialId) &&
                        args.UserHandle.AsSpan().SequenceEqual(credential.UserHandle))
                },
                cancellationToken: cancellationToken);

            if (credential.SignatureCounter > 0 && result.SignCount <= credential.SignatureCounter)
            {
                PasskeyMetrics.Record("interactive_assertion", "counter_regression");
                throw new SharedValidationException("Passkey assertion counter regression was detected.");
            }

            return new VerifiedPasskeyAssertion(
                credential,
                result.SignCount,
                result.IsBackedUp,
                DateTime.UtcNow,
                HasUserVerification(response.Response.AuthenticatorData));
        }
        catch (Fido2VerificationException exception)
        {
            var outcome = PasskeyVerificationException.BuildOutcome(exception.Code);
            PasskeyMetrics.Record("interactive_assertion", outcome);
            throw new PasskeyVerificationException(
                "Passkey assertion verification failed.",
                outcome);
        }
    }

    private static bool HasUserVerification(byte[] authenticatorData)
    {
        if (authenticatorData.Length < AuthenticatorDataMinimumLength)
        {
            throw new SharedValidationException("Passkey authenticator data is invalid.");
        }

        return (authenticatorData[AuthenticatorFlagsOffset] & UserVerifiedFlag) != 0;
    }

    public async Task<IReadOnlyList<IdentityPasskeyCredential>> ListUserCredentialsAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var query = unitOfWorkDbContext.IdentityPasskeyCredentials.Where(x => x.UserId == userId);
        query = tenantId.HasValue
            ? query.Where(x => x.TenantId == tenantId.Value)
            : query.Where(x => x.TenantId == null);
        return await query.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<int> CountUserCredentialsAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var query = unitOfWorkDbContext.IdentityPasskeyCredentials.Where(x => x.UserId == userId);
        query = tenantId.HasValue
            ? query.Where(x => x.TenantId == tenantId.Value)
            : query.Where(x => x.TenantId == null);
        return await query.CountAsync(cancellationToken);
    }

    public async Task<IdentityPasskeyCredential?> FindUserCredentialAsync(
        Guid userId,
        Guid? tenantId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var query = unitOfWorkDbContext.IdentityPasskeyCredentials.Where(x => x.UserId == userId && x.Id == credentialId);
        return await (tenantId.HasValue
                ? query.Where(x => x.TenantId == tenantId.Value)
                : query.Where(x => x.TenantId == null))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<IdentityPasskeyCredential?> FindUserCredentialByHashAsync(
        Guid userId,
        Guid? tenantId,
        string credentialHash,
        CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var query = unitOfWorkDbContext.IdentityPasskeyCredentials.Where(x =>
                    x.UserId == userId && x.CredentialIdHash == credentialHash);
        return await (tenantId.HasValue
                ? query.Where(x => x.TenantId == tenantId.Value)
                : query.Where(x => x.TenantId == null))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public static PasskeyCredentialDto ToDto(IdentityPasskeyCredential credential)
    {
        return new PasskeyCredentialDto
        {
            Id = credential.Id,
            FriendlyName = credential.FriendlyName,
            AaGuid = credential.AaGuid,
            Transports = DeserializeTransports(credential.Transports)
                .Select(x => x.ToString().ToLowerInvariant())
                .ToList(),
            IsBackupEligible = credential.IsBackupEligible,
            IsBackedUp = credential.IsBackedUp,
            CreatedAtUtc = credential.CreatedAtUtc,
            LastUsedAtUtc = credential.LastUsedAtUtc
        };
    }

    public static string NormalizeFriendlyName(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new SharedValidationException("Passkey name is required.");
        }

        return normalized.Length <= 128 ? normalized : normalized[..128];
    }

    private async Task<PasskeyCeremonyOptionsDto> StoreCeremonyAsync(
        ApplicationUser user,
        string friendlyName,
        PasskeyCeremonyPurpose purpose,
        Guid? interactiveChallengeId,
        string originalOptionsJson,
        CancellationToken cancellationToken)
    {
        var ceremonyId = Guid.NewGuid();
        var expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(
            Math.Clamp(_options.Value.CeremonyTtlSeconds, 60, 600));
        var item = new PasskeyCeremonyCacheItem
        {
            CeremonyId = ceremonyId,
            Purpose = purpose,
            UserId = user.Id,
            TenantId = NormalizeTenantId(user),
            InteractiveChallengeId = interactiveChallengeId,
            FriendlyName = friendlyName,
            OriginalOptionsJson = originalOptionsJson,
            ConfigurationFingerprint = BuildConfigurationFingerprint(_options.Value),
            ExpiresAtUtc = expiresAtUtc
        };

        await _ceremonyCache.SetAsync(
            BuildCacheKey(ceremonyId),
            item,
            new DistributedCacheEntryOptions { AbsoluteExpiration = expiresAtUtc },
            token: cancellationToken);

        return new PasskeyCeremonyOptionsDto
        {
            CeremonyId = ceremonyId,
            OptionsJson = originalOptionsJson,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    private async Task<PasskeyCeremonyCacheItem> ConsumeCeremonyAsync(
        Guid ceremonyId,
        ApplicationUser user,
        PasskeyCeremonyPurpose purpose,
        Guid? interactiveChallengeId,
        CancellationToken cancellationToken)
    {
        if (ceremonyId == Guid.Empty)
        {
            throw new SharedValidationException("Passkey ceremony is invalid or expired.");
        }

        await using var handle = await _distributedLock.TryAcquireAsync(
            $"identity:passkey:ceremony:lock:{ceremonyId:D}",
            CeremonyLockOptions,
            cancellationToken);
        if (handle == null)
        {
            PasskeyMetrics.Record(CeremonyOperation(purpose), "ceremony_busy");
            throw new SharedValidationException("Passkey ceremony is already being processed.");
        }

        var cacheKey = BuildCacheKey(ceremonyId);
        var item = await _ceremonyCache.GetAsync(cacheKey, token: cancellationToken);
        await _ceremonyCache.RemoveAsync(cacheKey, token: cancellationToken);

        var tenantId = NormalizeTenantId(user);
        if (item == null ||
            item.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            item.Purpose != purpose ||
            item.UserId != user.Id ||
            item.TenantId != tenantId ||
            item.InteractiveChallengeId != interactiveChallengeId ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(item.ConfigurationFingerprint),
                Encoding.UTF8.GetBytes(BuildConfigurationFingerprint(_options.Value))))
        {
            PasskeyMetrics.Record(CeremonyOperation(purpose), "ceremony_rejected");
            throw new SharedValidationException("Passkey ceremony is invalid or expired.");
        }

        return item;
    }

    private static PublicKeyCredentialDescriptor ToDescriptor(IdentityPasskeyCredential credential)
    {
        return new PublicKeyCredentialDescriptor(
            PublicKeyCredentialType.PublicKey,
            credential.CredentialId,
            DeserializeTransports(credential.Transports));
    }

    private static Fido2User CreateFidoUser(ApplicationUser user)
    {
        return new Fido2User
        {
            Id = BuildUserHandle(user),
            Name = user.UserName ?? user.Id.ToString("D"),
            DisplayName = string.IsNullOrWhiteSpace(user.Name)
                ? user.UserName ?? "TenantWright user"
                : user.Name
        };
    }

    private static byte[] BuildUserHandle(ApplicationUser user)
    {
        var tenant = NormalizeTenantId(user)?.ToString("D") ?? "host";
        return SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant}:{user.Id:D}"));
    }

    private static Guid? NormalizeTenantId(ApplicationUser user)
        => user.TenantId == Guid.Empty ? null : user.TenantId;

    private static string ComputeCredentialIdHash(byte[] credentialId)
        => Convert.ToHexString(SHA256.HashData(credentialId)).ToLowerInvariant();

    private static string BuildConfigurationFingerprint(PasskeyOptions options)
    {
        var origins = string.Join("|", options.Origins.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{options.RelyingPartyId}|{origins}"))).ToLowerInvariant();
    }

    private static string? SerializeTransports(AuthenticatorTransport[]? transports)
        => transports is { Length: > 0 } ? JsonSerializer.Serialize(transports, SerializerOptions) : null;

    private static AuthenticatorTransport[] DeserializeTransports(string? transports)
    {
        if (string.IsNullOrWhiteSpace(transports))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<AuthenticatorTransport[]>(transports, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static T Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 65536)
        {
            throw new SharedValidationException("Passkey response is invalid.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                   ?? throw new SharedValidationException("Passkey response is invalid.");
        }
        catch (JsonException)
        {
            throw new SharedValidationException("Passkey response is invalid.");
        }
    }

    private void EnsureEnabled()
    {
        if (!_options.Value.Enabled)
        {
            throw new SharedValidationException("Passkeys are not enabled.");
        }
    }

    private static string BuildCacheKey(Guid ceremonyId)
        => $"identity:passkey:ceremony:{ceremonyId:D}";

    private static SharedValidationException PasskeyAssertionRejected(string outcome)
    {
        PasskeyMetrics.Record("interactive_assertion", outcome);
        return new SharedValidationException("Passkey assertion verification failed.");
    }

    private static string RegistrationOperation(PasskeyCeremonyPurpose purpose)
        => purpose == PasskeyCeremonyPurpose.InteractiveEnrollment
            ? "interactive_enrollment"
            : "register";

    private static string CeremonyOperation(PasskeyCeremonyPurpose purpose)
        => purpose switch
        {
            PasskeyCeremonyPurpose.InteractiveEnrollment => "interactive_enrollment",
            PasskeyCeremonyPurpose.InteractiveAssertion => "interactive_assertion",
            _ => "register"
        };
}

internal sealed record VerifiedPasskeyAssertion(
    IdentityPasskeyCredential Credential,
    uint SignatureCounter,
    bool IsBackedUp,
    DateTime LastUsedAtUtc,
    bool UserVerified)
{
    public void Apply()
    {
        Credential.SignatureCounter = SignatureCounter;
        Credential.IsBackedUp = IsBackedUp;
        Credential.LastUsedAtUtc = LastUsedAtUtc;
        Credential.ConcurrencyStamp = Guid.NewGuid().ToString("N");
    }
}

internal sealed class PasskeyVerificationException : SharedValidationException
{
    public PasskeyVerificationException(string message, string outcome)
        : base(message)
    {
        Outcome = outcome;
    }

    public string Outcome { get; }

    public static string BuildOutcome(Fido2NetLib.Exceptions.Fido2ErrorCode code)
        => $"fido_{code}";
}
