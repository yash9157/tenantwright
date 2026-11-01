using System.Net;
using System.Linq;
using System.Net.Http.Headers;
using System.Text.Json;
using Auth.Caching;
using Auth.Clients.Caching;
using Identity.Application.Contracts.Account;
using Identity.Domain.Shared.Account;
using Identity.Application.Contracts.Users;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using Shared.Caching.Abstractions;
using Shared.Caching.Sessions;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;

namespace Auth.Clients;

public interface IIdentityClient :
    IIdentityAuthenticationClient,
    IIdentityPasswordClient,
    IIdentityUserQueryClient,
    IIdentitySessionClient;

public partial class IdentityClient : IIdentityClient
{
    private const string AuthzSegment = "authz";
    private const string ProfileSegment = "profile";
    private static readonly TimeSpan BackgroundRefreshDelay = TimeSpan.FromMilliseconds(25);

    private readonly HttpClient _httpClient;
    private readonly IAuthHybridCache<IdentityProfileCacheEnvelope> _profileCache;
    private readonly IAuthHybridCache<IdentityAuthorizationCacheEnvelope> _authorizationCache;
    private readonly IAuthHybridCache<IdentityAliasCacheEnvelope> _aliasCache;
    private readonly ILogger<IdentityClient> _logger;
    private readonly IInternalAccessTokenProvider _tokenProvider;
    private readonly IdentityUserCacheOptions _cacheOptions;
    private readonly OpenIddictServerOptions _openIddictOptions;
    private readonly ISessionMarkerStore _sessionMarkerStore;
    private readonly SessionValidationOptions _sessionValidationOptions;
    private readonly IDistributedLock _distributedLock;

    public IdentityClient(
        HttpClient httpClient,
        IConfiguration configuration,
        IAuthHybridCache<IdentityProfileCacheEnvelope> profileCache,
        IAuthHybridCache<IdentityAuthorizationCacheEnvelope> authorizationCache,
        IAuthHybridCache<IdentityAliasCacheEnvelope> aliasCache,
        ILogger<IdentityClient> logger,
        IInternalAccessTokenProvider tokenProvider,
        IOptions<IdentityUserCacheOptions> cacheOptions,
        IOptions<OpenIddictServerOptions> openIddictOptions,
        IOptions<SessionValidationOptions> sessionValidationOptions,
        ISessionMarkerStore sessionMarkerStore,
        IDistributedLock distributedLock)
    {
        _httpClient = httpClient;
        var baseAddress = configuration["Services:Identity:Url"];
        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            throw new InvalidOperationException("Services:Identity:Url is required.");
        }

        _httpClient.BaseAddress = new Uri(baseAddress);
        _profileCache = profileCache;
        _authorizationCache = authorizationCache;
        _aliasCache = aliasCache;
        _logger = logger;
        _tokenProvider = tokenProvider;
        _cacheOptions = cacheOptions.Value;
        _openIddictOptions = openIddictOptions.Value;
        _sessionValidationOptions = sessionValidationOptions.Value;
        _sessionMarkerStore = sessionMarkerStore;
        _distributedLock = distributedLock;
    }

    private async Task<IdentityProfileCacheEnvelope?> EnsureProfileAsync(
        IdentityProfileCacheEnvelope? existing,
        Guid userId,
        Guid? tenantId,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!bypassCache && existing != null)
        {
            if (!IsSoftExpired(existing.SoftExpireAtUtc, now))
            {
                return existing;
            }

            if (!IsHardExpired(existing.HardExpireAtUtc, now))
            {
                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await Task.Delay(BackgroundRefreshDelay).ConfigureAwait(false);
                            await RefreshSegmentAsync(userId, tenantId, UserInfoSegments.Profile, existing.ETag, strict: false, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Identity profile background refresh failed.");
                        }
                    },
                    CancellationToken.None);

                return existing;
            }
        }

        var refreshed = await RefreshSegmentAsync(
            userId,
            tenantId,
            UserInfoSegments.Profile,
            existing?.ETag,
            strict: true,
            cancellationToken);

        if (refreshed.Status == SnapshotFetchStatus.NotModified && existing != null)
        {
            var latest = await _profileCache.GetAsync(
                IdentityUserCacheKeys.Profile(tenantId, userId),
                token: cancellationToken);
            var latestNow = DateTimeOffset.UtcNow;
            if (latest != null && !IsSoftExpired(latest.SoftExpireAtUtc, latestNow))
            {
                return latest;
            }

            var extended = ExtendProfileEnvelope(existing);
            await _profileCache.SetAsync(
                IdentityUserCacheKeys.Profile(tenantId, userId),
                extended,
                options: CreateHardTtlOptions(extended.HardExpireAtUtc),
                token: cancellationToken);
            return extended;
        }

        if (refreshed.Status == SnapshotFetchStatus.Success && refreshed.UserInfo != null)
        {
            return await UpsertProfileAsync(refreshed.UserInfo, refreshed.ETag, cancellationToken);
        }

        return null;
    }

    private async Task<IdentityAuthorizationCacheEnvelope?> EnsureAuthorizationAsync(
        IdentityAuthorizationCacheEnvelope? existing,
        Guid userId,
        Guid? tenantId,
        bool strictAuthorization,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!bypassCache && existing != null)
        {
            if (!IsSoftExpired(existing.SoftExpireAtUtc, now))
            {
                return existing;
            }

            if (!IsHardExpired(existing.HardExpireAtUtc, now))
            {
                if (!strictAuthorization)
                {
                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                await Task.Delay(BackgroundRefreshDelay).ConfigureAwait(false);
                                await RefreshSegmentAsync(userId, tenantId, UserInfoSegments.Authorization, existing.ETag, strict: false, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Identity authorization background refresh failed.");
                            }
                        },
                        CancellationToken.None);

                    return existing;
                }
            }
        }

        var refreshed = await RefreshSegmentAsync(
            userId,
            tenantId,
            UserInfoSegments.Authorization,
            existing?.ETag,
            strict: strictAuthorization,
            cancellationToken);

        if (refreshed.Status == SnapshotFetchStatus.NotModified && existing != null)
        {
            var latest = await _authorizationCache.GetAsync(
                IdentityUserCacheKeys.Authz(tenantId, userId),
                token: cancellationToken);
            var latestNow = DateTimeOffset.UtcNow;
            if (latest != null && !IsSoftExpired(latest.SoftExpireAtUtc, latestNow))
            {
                return latest.Payload.IsActive && !latest.Payload.IsLockedOut
                    ? latest
                    : null;
            }

            var extended = ExtendAuthorizationEnvelope(existing);
            await _authorizationCache.SetAsync(
                IdentityUserCacheKeys.Authz(tenantId, userId),
                extended,
                options: CreateHardTtlOptions(extended.HardExpireAtUtc),
                token: cancellationToken);
            return extended;
        }

        if (refreshed.Status == SnapshotFetchStatus.Success && refreshed.UserInfo != null)
        {
            var envelope = await UpsertAuthorizationAsync(refreshed.UserInfo, refreshed.ETag, cancellationToken);
            return envelope.Payload.IsActive && !envelope.Payload.IsLockedOut
                ? envelope
                : null;
        }

        if (!strictAuthorization && existing != null && !IsHardExpired(existing.HardExpireAtUtc, now))
        {
            return existing;
        }

        return null;
    }

    private async Task<SnapshotFetchResult> RefreshSegmentAsync(
        Guid userId,
        Guid? tenantId,
        UserInfoSegments segment,
        string? etag,
        bool strict,
        CancellationToken cancellationToken)
    {
        var lockKey = IdentityUserCacheKeys.Lock(tenantId, userId, segment == UserInfoSegments.Profile ? ProfileSegment : AuthzSegment);
        IDistributedLockHandle? lease;
        try
        {
            lease = await TryAcquireRefreshLockAsync(lockKey, strict, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity snapshot refresh lock acquisition failed in strict mode.");
            return SnapshotFetchResult.Failed();
        }

        if (lease == null)
        {
            if (!strict)
            {
                return SnapshotFetchResult.Failed();
            }

            var waitTimeout = Math.Max(100, _cacheOptions.StrictRefreshWaitMilliseconds);
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(waitTimeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(75, cancellationToken);
                var cacheRefreshed = await ReadSegmentEnvelopeFreshAsync(userId, tenantId, segment, cancellationToken);
                if (cacheRefreshed)
                {
                    return SnapshotFetchResult.NotModified(etag);
                }
            }

            return SnapshotFetchResult.Failed();
        }

        await using (lease)
        {
            return await FetchSnapshotByIdAsync(userId, tenantId, segment, etag, cancellationToken);
        }
    }

    private async Task<bool> ReadSegmentEnvelopeFreshAsync(Guid userId, Guid? tenantId, UserInfoSegments segment, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (segment == UserInfoSegments.Profile)
        {
            var envelope = await _profileCache.GetAsync(
                IdentityUserCacheKeys.Profile(tenantId, userId),
                token: cancellationToken);
            return envelope != null && !IsSoftExpired(envelope.SoftExpireAtUtc, now);
        }

        var authzEnvelope = await _authorizationCache.GetAsync(
            IdentityUserCacheKeys.Authz(tenantId, userId),
            token: cancellationToken);
        return authzEnvelope != null && !IsSoftExpired(authzEnvelope.SoftExpireAtUtc, now);
    }

    private async Task<UserInfoDto?> FetchUserInfoByLoginAsync(
        string loginInput,
        Guid? tenantId,
        InteractiveLoginMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            var encodedLoginInput = Uri.EscapeDataString(loginInput);
            var requestUri = $"api/identity/account/user-info?loginInput={encodedLoginInput}&mode={Uri.EscapeDataString(mode.ToString())}";
            if (mode == InteractiveLoginMode.Tenant && tenantId.HasValue)
            {
                requestUri += $"&tenantId={tenantId.Value:D}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Identity service get-user-info failed. Status: {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<UserInfoDto>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for get-user-info.");
            return null;
        }
    }

    private async Task<SnapshotFetchResult> FetchSnapshotByIdAsync(
        Guid userId,
        Guid? tenantId,
        UserInfoSegments segments,
        string? etag,
        CancellationToken cancellationToken)
    {
        try
        {
            var segmentQuery = Uri.EscapeDataString(UserInfoEtagHelper.ToQueryString(segments));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/identity/account/user-info/by-id?userId={userId:D}&segments={segmentQuery}");
            if (!string.IsNullOrWhiteSpace(etag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return SnapshotFetchResult.NotModified(response.Headers.ETag?.ToString() ?? etag);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return SnapshotFetchResult.NotFound();
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Identity service get-user-info-by-id failed. Status: {StatusCode}", response.StatusCode);
                return SnapshotFetchResult.Failed();
            }

            var userInfo = await response.Content.ReadFromJsonAsync<UserInfoDto>(cancellationToken: cancellationToken);
            if (userInfo == null)
            {
                return SnapshotFetchResult.Failed();
            }

            var responseEtag = response.Headers.ETag?.ToString();
            return SnapshotFetchResult.Success(userInfo, responseEtag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for get-user-info-by-id.");
            return SnapshotFetchResult.Failed();
        }
    }

    private async Task<IdentityProfileCacheEnvelope> UpsertProfileAsync(UserInfoDto userInfo, string? etag, CancellationToken cancellationToken)
    {
        var envelope = CreateProfileEnvelope(userInfo, etag);
        await _profileCache.SetAsync(
            IdentityUserCacheKeys.Profile(userInfo.TenantId, userInfo.Id),
            envelope,
            options: CreateHardTtlOptions(envelope.HardExpireAtUtc),
            token: cancellationToken);
        return envelope;
    }

    private async Task<IdentityAuthorizationCacheEnvelope> UpsertAuthorizationAsync(UserInfoDto userInfo, string? etag, CancellationToken cancellationToken)
    {
        var envelope = CreateAuthorizationEnvelope(userInfo, etag);
        await _authorizationCache.SetAsync(
            IdentityUserCacheKeys.Authz(userInfo.TenantId, userInfo.Id),
            envelope,
            options: CreateHardTtlOptions(envelope.HardExpireAtUtc),
            token: cancellationToken);
        return envelope;
    }

    private async Task<IdentityAliasCacheEnvelope> UpsertAliasAsync(
        string aliasKey,
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var envelope = CreateAliasEnvelope(userId, tenantId);
        await _aliasCache.SetAsync(
            aliasKey,
            envelope,
            options: CreateHardTtlOptions(envelope.HardExpireAtUtc),
            token: cancellationToken);
        return envelope;
    }

    private async Task RefreshAliasAsync(string loginInput, Guid? tenantId, InteractiveLoginMode mode)
    {
        var refreshed = await FetchUserInfoByLoginAsync(loginInput, tenantId, mode, CancellationToken.None).ConfigureAwait(false);
        if (refreshed == null)
        {
            return;
        }

        var aliasKey = IdentityUserCacheKeys.Alias(tenantId, loginInput);
        await UpsertAliasAsync(aliasKey, refreshed.Id, refreshed.TenantId, CancellationToken.None).ConfigureAwait(false);
        await UpsertProfileAsync(refreshed, null, CancellationToken.None).ConfigureAwait(false);
        await UpsertAuthorizationAsync(refreshed, null, CancellationToken.None).ConfigureAwait(false);
    }

    private IdentityProfileCacheEnvelope CreateProfileEnvelope(UserInfoDto userInfo, string? etag)
    {
        var now = DateTimeOffset.UtcNow;
        var (softTtl, hardTtl) = ComputeProfileTtls();

        return new IdentityProfileCacheEnvelope
        {
            Payload = new IdentityProfileCacheItem
            {
                UserId = userInfo.Id,
                TenantId = userInfo.TenantId,
                UserName = userInfo.UserName,
                Email = userInfo.Email,
                PhoneNumber = userInfo.PhoneNumber,
                ProfileVersion = userInfo.ProfileVersion
            },
            FetchedAtUtc = now,
            SoftExpireAtUtc = now.Add(softTtl),
            HardExpireAtUtc = now.Add(hardTtl),
            ETag = etag
        };
    }

    private IdentityAuthorizationCacheEnvelope CreateAuthorizationEnvelope(UserInfoDto userInfo, string? etag)
    {
        var now = DateTimeOffset.UtcNow;
        var (softTtl, hardTtl) = ComputeAuthorizationTtls();

        return new IdentityAuthorizationCacheEnvelope
        {
            Payload = new IdentityAuthorizationCacheItem
            {
                UserId = userInfo.Id,
                TenantId = userInfo.TenantId,
                Roles = userInfo.Roles.Distinct(StringComparer.Ordinal).ToList(),
                RoleIds = userInfo.RoleIds.Distinct().OrderBy(x => x).ToList(),
                IsActive = userInfo.IsActive,
                IsLockedOut = userInfo.IsLockedOut,
                AuthzVersion = userInfo.AuthzVersion
            },
            FetchedAtUtc = now,
            SoftExpireAtUtc = now.Add(softTtl),
            HardExpireAtUtc = now.Add(hardTtl),
            ETag = etag
        };
    }

    private IdentityAliasCacheEnvelope CreateAliasEnvelope(Guid userId, Guid? tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        var (softTtl, hardTtl) = ComputeAliasTtls();

        return new IdentityAliasCacheEnvelope
        {
            Payload = new IdentityAliasCacheItem
            {
                UserId = userId,
                TenantId = tenantId
            },
            FetchedAtUtc = now,
            SoftExpireAtUtc = now.Add(softTtl),
            HardExpireAtUtc = now.Add(hardTtl)
        };
    }

    private IdentityProfileCacheEnvelope ExtendProfileEnvelope(IdentityProfileCacheEnvelope existing)
    {
        var now = DateTimeOffset.UtcNow;
        var (softTtl, hardTtl) = ComputeProfileTtls();
        return new IdentityProfileCacheEnvelope
        {
            Payload = existing.Payload,
            FetchedAtUtc = now,
            SoftExpireAtUtc = now.Add(softTtl),
            HardExpireAtUtc = now.Add(hardTtl),
            ETag = existing.ETag
        };
    }

    private IdentityAuthorizationCacheEnvelope ExtendAuthorizationEnvelope(IdentityAuthorizationCacheEnvelope existing)
    {
        var now = DateTimeOffset.UtcNow;
        var (softTtl, hardTtl) = ComputeAuthorizationTtls();
        return new IdentityAuthorizationCacheEnvelope
        {
            Payload = existing.Payload,
            FetchedAtUtc = now,
            SoftExpireAtUtc = now.Add(softTtl),
            HardExpireAtUtc = now.Add(hardTtl),
            ETag = existing.ETag
        };
    }

    private static UserInfoDto ToUserInfo(IdentityProfileCacheItem profile, IdentityAuthorizationCacheItem authz)
    {
        return new UserInfoDto
        {
            Id = profile.UserId,
            TenantId = profile.TenantId,
            UserName = profile.UserName,
            Email = profile.Email,
            PhoneNumber = profile.PhoneNumber,
            Roles = authz.Roles,
            RoleIds = authz.RoleIds,
            IsActive = authz.IsActive,
            IsLockedOut = authz.IsLockedOut,
            ProfileVersion = profile.ProfileVersion,
            AuthzVersion = authz.AuthzVersion
        };
    }

    private static bool IsSoftExpired(DateTimeOffset softExpireAtUtc, DateTimeOffset now)
        => now >= softExpireAtUtc;

    private static bool IsHardExpired(DateTimeOffset hardExpireAtUtc, DateTimeOffset now)
        => now >= hardExpireAtUtc;

    private static HybridCacheEntryOptions CreateHardTtlOptions(DateTimeOffset hardExpireAtUtc)
    {
        var ttl = hardExpireAtUtc - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero)
        {
            ttl = TimeSpan.FromSeconds(1);
        }

        return new HybridCacheEntryOptions
        {
            LocalCacheExpiration = ttl,
            Expiration = ttl
        };
    }

    private (TimeSpan SoftTtl, TimeSpan HardTtl) ComputeProfileTtls()
    {
        var soft = ApplyJitter(TimeSpan.FromMinutes(Math.Max(1, _cacheOptions.ProfileSoftTtlMinutes)));
        var hard = ApplyJitter(TimeSpan.FromMinutes(Math.Max(2, _cacheOptions.ProfileHardTtlMinutes)));
        if (hard < soft)
        {
            hard = soft;
        }

        return (soft, hard);
    }

    private (TimeSpan SoftTtl, TimeSpan HardTtl) ComputeAliasTtls()
    {
        var soft = ApplyJitter(TimeSpan.FromMinutes(Math.Max(1, _cacheOptions.AliasSoftTtlMinutes)));
        var hard = ApplyJitter(TimeSpan.FromMinutes(Math.Max(2, _cacheOptions.AliasHardTtlMinutes)));
        if (hard < soft)
        {
            hard = soft;
        }

        return (soft, hard);
    }

    private (TimeSpan SoftTtl, TimeSpan HardTtl) ComputeAuthorizationTtls()
    {
        var accessLifetime = _openIddictOptions.AccessTokenLifetime ?? TimeSpan.FromMinutes(15);
        if (accessLifetime <= TimeSpan.Zero)
        {
            accessLifetime = TimeSpan.FromMinutes(15);
        }

        var hardCap = TimeSpan.FromMinutes(Math.Max(1, _cacheOptions.AuthzHardTtlUpperBoundMinutes));
        var hardBase = accessLifetime < hardCap ? accessLifetime : hardCap;

        var hard = ApplyJitter(hardBase);
        if (hard > hardBase)
        {
            hard = hardBase;
        }

        if (hard < TimeSpan.FromMinutes(1))
        {
            hard = TimeSpan.FromMinutes(1);
        }

        var softFromRatio = TimeSpan.FromTicks((long)(hard.Ticks * 0.20d));
        var softMin = TimeSpan.FromMinutes(Math.Max(1, _cacheOptions.AuthzSoftTtlMinMinutes));
        var softMax = TimeSpan.FromMinutes(Math.Max(_cacheOptions.AuthzSoftTtlMinMinutes, _cacheOptions.AuthzSoftTtlMaxMinutes));
        var soft = softFromRatio;

        if (soft < softMin)
        {
            soft = softMin;
        }
        if (soft > softMax)
        {
            soft = softMax;
        }
        if (soft > hard)
        {
            soft = hard;
        }

        soft = ApplyJitter(soft);
        if (soft > hard)
        {
            soft = hard;
        }

        if (soft < TimeSpan.FromSeconds(30))
        {
            soft = TimeSpan.FromSeconds(30);
        }

        return (soft, hard);
    }

    private TimeSpan ApplyJitter(TimeSpan value)
    {
        var jitterPercent = Math.Clamp(_cacheOptions.JitterPercent, 0, 50);
        if (jitterPercent == 0)
        {
            return value;
        }

        var spread = jitterPercent / 100d;
        var minFactor = 1d - spread;
        var maxFactor = 1d + spread;
        var factor = minFactor + (Random.Shared.NextDouble() * (maxFactor - minFactor));
        var jitteredTicks = (long)(value.Ticks * factor);

        return TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, jitteredTicks));
    }

    private async Task AddInternalAuthorizationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Guid? tenantId = null;
        if (request.Headers.TryGetValues("X-Tenant-Id", out var values) &&
            Guid.TryParse(values.SingleOrDefault(), out var parsedTenantId) &&
            parsedTenantId != Guid.Empty)
        {
            tenantId = parsedTenantId;
        }

        var token = tenantId.HasValue
            ? await _tokenProvider.GetAccessTokenAsync("identity-internal", tenantId.Value, cancellationToken)
            : await _tokenProvider.GetAccessTokenAsync("identity-internal", cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static void AddTenantHeader(HttpRequestMessage request, Guid? tenantId)
    {
        if (!tenantId.HasValue || tenantId.Value == Guid.Empty)
        {
            return;
        }

        request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantId.Value.ToString("D"));
    }

    private Task<IDistributedLockHandle?> TryAcquireRefreshLockAsync(
        string lockKey,
        bool strict,
        CancellationToken cancellationToken)
    {
        return _distributedLock.TryAcquireAsync(
            lockKey,
            new DistributedLockAcquireOptions
            {
                PolicyName = DistributedLockPolicyNames.AuthIdentitySnapshotRefresh,
                IgnoreTenant = true,
                LeaseMs = Math.Max(1000, _cacheOptions.LockTtlSeconds * 1000),
                FailureMode = strict ? DistributedLockFailureMode.FailClosed : DistributedLockFailureMode.PerUse
            },
            cancellationToken);
    }

    private static async Task<string?> TryReadErrorDescriptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            if (json.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (json.TryGetProperty("error_description", out var errorDescription))
            {
                return errorDescription.GetString();
            }

            if (json.TryGetProperty("error", out var error))
            {
                return error.GetString();
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private sealed class SnapshotFetchResult
    {
        public SnapshotFetchStatus Status { get; }
        public UserInfoDto? UserInfo { get; }
        public string? ETag { get; }

        private SnapshotFetchResult(SnapshotFetchStatus status, UserInfoDto? userInfo = null, string? etag = null)
        {
            Status = status;
            UserInfo = userInfo;
            ETag = etag;
        }

        public static SnapshotFetchResult Failed()
            => new(SnapshotFetchStatus.Failed);

        public static SnapshotFetchResult NotFound()
            => new(SnapshotFetchStatus.NotFound);

        public static SnapshotFetchResult NotModified(string? etag)
            => new(SnapshotFetchStatus.NotModified, etag: etag);

        public static SnapshotFetchResult Success(UserInfoDto userInfo, string? etag)
            => new(SnapshotFetchStatus.Success, userInfo, etag);
    }

    private enum SnapshotFetchStatus
    {
        Failed = 0,
        NotFound = 1,
        NotModified = 2,
        Success = 3
    }
}
