using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shared.Caching;
using Shared.Caching.Abstractions;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;
using Shared.Modularity.DependencyInjection;

namespace Shared.Permissions.Features;

/// <summary>
/// Cached, distributed-lock-protected M2M client-credentials token acquisition for internal
/// service-to-service calls. Reads <c>AuthServer:Authority</c> and
/// <c>OpenIddict:Clients:{profile}:{ClientId,Secret}</c> for the profile the caller names. Consolidates what
/// used to be near-identical per-service uncached implementations of internal reference-validation clients.
/// </summary>
/// <remarks>
/// Named client profiles exist so a service can call outbound internal endpoints under its own
/// service identity instead of borrowing another service's credential, which keeps audit attribution
/// honest and lets each downstream allow-list exactly one caller. The profile name is a configuration
/// section name owned by the calling service (bound from its client options); this library holds no
/// per-service names and offers no implicit default profile, so a host configured only with its own
/// profile can never silently request a token under a profile it does not have.
/// </remarks>
public interface IInternalAccessTokenProvider
{
    /// <summary>
    /// Acquires a token under a named client profile bound to
    /// <c>OpenIddict:Clients:{clientProfileName}</c>. Returns <c>null</c> when the profile is missing or
    /// incomplete so callers fail closed rather than falling back to another identity.
    /// </summary>
    Task<string?> GetAccessTokenForClientAsync(
        string clientProfileName,
        string scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires a token under a named client profile for one explicit tenant. Implementations that have
    /// not adopted tenant-bound client credentials fail closed instead of accidentally issuing a
    /// tenantless token for a tenant-scoped internal request.
    /// </summary>
    Task<string?> GetAccessTokenForClientAsync(
        string clientProfileName,
        string scope,
        Guid tenantId,
        CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

public sealed class InternalAccessTokenProvider : IInternalAccessTokenProvider, ISingletonDependency
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IDistributedCache<InternalAccessTokenCacheItem, string> _tokenCache;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<InternalAccessTokenProvider> _logger;

    public InternalAccessTokenProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IDistributedCache<InternalAccessTokenCacheItem, string> tokenCache,
        IDistributedLock distributedLock,
        ILogger<InternalAccessTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _tokenCache = tokenCache;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    public async Task<string?> GetAccessTokenForClientAsync(
        string clientProfileName,
        string scope,
        CancellationToken cancellationToken = default)
    {
        return await GetAccessTokenForClientAsync(
            clientProfileName,
            scope,
            tenantId: null,
            cancellationToken);
    }

    public async Task<string?> GetAccessTokenForClientAsync(
        string clientProfileName,
        string scope,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            _logger.LogWarning("Internal access token tenant is missing.");
            return null;
        }

        return await GetAccessTokenForClientAsync(clientProfileName, scope, (Guid?)tenantId, cancellationToken);
    }

    private async Task<string?> GetAccessTokenForClientAsync(
        string clientProfileName,
        string scope,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        if (!IsValidClientProfileName(clientProfileName))
        {
            _logger.LogWarning("Internal access token client profile name is invalid.");
            return null;
        }

        var profile = clientProfileName.Trim();
        var normalizedScope = scope.Trim();
        var now = DateTimeOffset.UtcNow;

        try
        {
            var authority = _configuration["AuthServer:Authority"]?.TrimEnd('/');
            var clientId = _configuration[$"OpenIddict:Clients:{profile}:ClientId"];
            var clientSecret = _configuration[$"OpenIddict:Clients:{profile}:Secret"];

            if (string.IsNullOrWhiteSpace(authority) ||
                string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogWarning(
                    "Internal access token configuration is incomplete for client profile {ClientProfile}.",
                    profile);
                return null;
            }

            if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
            {
                _logger.LogWarning("AuthServer:Authority is invalid for internal access token flow.");
                return null;
            }

            var cacheKey = BuildTokenCacheKey(authorityUri, clientId, normalizedScope, tenantId);
            var existing = await _tokenCache.GetAsync(cacheKey, token: cancellationToken);
            if (IsTokenFresh(existing, now))
            {
                return existing!.Token;
            }

            await using var refreshLock = await _distributedLock.TryAcquireAsync(
                BuildTokenLockKey(clientId, normalizedScope, tenantId),
                new DistributedLockAcquireOptions
                {
                    PolicyName = DistributedLockPolicyNames.M2mTokenAcquisition,
                    IgnoreTenant = true
                },
                cancellationToken);

            if (refreshLock == null)
            {
                var sharedToken = await WaitForSharedTokenAsync(cacheKey, cancellationToken);
                return sharedToken?.Token;
            }

            now = DateTimeOffset.UtcNow;
            existing = await _tokenCache.GetAsync(cacheKey, token: cancellationToken);
            if (IsTokenFresh(existing, now))
            {
                return existing!.Token;
            }

            var client = _httpClientFactory.CreateClient(nameof(InternalAccessTokenProvider));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(authorityUri, "/connect/token"))
            {
                Content = new FormUrlEncodedContent(BuildTokenRequestForm(clientId, clientSecret, normalizedScope, tenantId))
            };

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Internal access token request failed. Status: {StatusCode}", response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<AccessTokenResponse>(cancellationToken: cancellationToken);
            if (payload == null || string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                _logger.LogWarning("Internal access token response is missing access_token.");
                return null;
            }

            var expiresInSeconds = payload.ExpiresIn > 0 ? payload.ExpiresIn : 60;
            var entry = new InternalAccessTokenCacheItem
            {
                Token = payload.AccessToken,
                ExpiresAtUtc = now.AddSeconds(expiresInSeconds)
            };

            await _tokenCache.SetAsync(
                cacheKey,
                entry,
                options: BuildCacheEntryOptions(now, entry.ExpiresAtUtc),
                token: cancellationToken);

            return entry.Token;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Internal access token retrieval timed out or was canceled by an internal dependency.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Internal access token retrieval failed.");
            return null;
        }
    }

    private sealed class AccessTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private static bool IsTokenFresh(InternalAccessTokenCacheItem? entry, DateTimeOffset nowUtc)
    {
        return entry != null &&
               !string.IsNullOrWhiteSpace(entry.Token) &&
               entry.ExpiresAtUtc > nowUtc.AddSeconds(30);
    }

    /// <summary>
    /// A profile name becomes part of a configuration key path, so only simple identifier characters
    /// are accepted. This keeps a caller from reaching into an unrelated configuration section.
    /// </summary>
    private static bool IsValidClientProfileName(string? clientProfileName)
    {
        if (string.IsNullOrWhiteSpace(clientProfileName))
        {
            return false;
        }

        foreach (var character in clientProfileName.Trim())
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildTokenCacheKey(Uri authorityUri, string clientId, string scope, Guid? tenantId)
    {
        var tenantKey = tenantId?.ToString("D");
        return string.IsNullOrWhiteSpace(tenantKey)
            ? $"shared:internal-token:{authorityUri.Host}:{clientId}:{scope}"
            : $"shared:internal-token:{authorityUri.Host}:{clientId}:{scope}:{tenantKey}";
    }

    private static string BuildTokenLockKey(string clientId, string scope, Guid? tenantId)
    {
        var tenantKey = tenantId?.ToString("D");
        return string.IsNullOrWhiteSpace(tenantKey)
            ? $"shared:internal-token:{clientId}:{scope}"
            : $"shared:internal-token:{clientId}:{scope}:{tenantKey}";
    }

    private static Dictionary<string, string?> BuildTokenRequestForm(
        string clientId,
        string clientSecret,
        string scope,
        Guid? tenantId)
    {
        var form = new Dictionary<string, string?>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope
        };

        if (tenantId.HasValue)
        {
            form["tenant_id"] = tenantId.Value.ToString("D");
        }

        return form;
    }

    private static DistributedCacheEntryOptions BuildCacheEntryOptions(DateTimeOffset nowUtc, DateTimeOffset expiresAtUtc)
    {
        var ttl = expiresAtUtc - nowUtc;
        if (ttl <= TimeSpan.FromSeconds(1))
        {
            ttl = TimeSpan.FromSeconds(1);
        }

        return new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        };
    }

    private async Task<InternalAccessTokenCacheItem?> WaitForSharedTokenAsync(string cacheKey, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(75, cancellationToken);
            var candidate = await _tokenCache.GetAsync(cacheKey, token: cancellationToken);
            if (IsTokenFresh(candidate, DateTimeOffset.UtcNow))
            {
                return candidate;
            }
        }

        return null;
    }
}

[IgnoreCacheMultiTenancy]
public sealed class InternalAccessTokenCacheItem
{
    public string? Token { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
