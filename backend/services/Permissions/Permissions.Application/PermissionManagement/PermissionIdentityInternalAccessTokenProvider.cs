using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Caching;
using Shared.Caching.Abstractions;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.PermissionManagement;

internal sealed class PermissionIdentityInternalAccessTokenProvider : ITransientDependency
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IOptions<IdentityPermissionTargetValidationOptions> _options;
    private readonly IDistributedCache<PermissionIdentityInternalTokenCacheEntry, string> _tokenCache;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<PermissionIdentityInternalAccessTokenProvider> _logger;

    public PermissionIdentityInternalAccessTokenProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IOptions<IdentityPermissionTargetValidationOptions> options,
        IDistributedCache<PermissionIdentityInternalTokenCacheEntry, string> tokenCache,
        IDistributedLock distributedLock,
        ILogger<PermissionIdentityInternalAccessTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _options = options;
        _tokenCache = tokenCache;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    public async Task<string?> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        var normalizedScope = scope.Trim();

        try
        {
            var credentials = ResolveClientCredentials();
            if (credentials == null)
            {
                _logger.LogWarning("Identity permission target validation token configuration is incomplete.");
                return null;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var cacheKey = BuildTokenCacheKey(credentials.Value.ClientId, normalizedScope);
            var existing = await _tokenCache.GetAsync(cacheKey, token: cancellationToken);
            if (IsTokenFresh(existing, nowUtc))
            {
                return existing!.Token;
            }

            await using var refreshLock = await _distributedLock.TryAcquireAsync(
                $"permissions:identity-internal-token:{credentials.Value.ClientId}:{normalizedScope}",
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

            nowUtc = DateTimeOffset.UtcNow;
            existing = await _tokenCache.GetAsync(cacheKey, token: cancellationToken);
            if (IsTokenFresh(existing, nowUtc))
            {
                return existing!.Token;
            }

            var tokenResponse = await RequestTokenAsync(credentials.Value, normalizedScope, cancellationToken);
            if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                return null;
            }

            var expiresInSeconds = tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : 60;
            var entry = new PermissionIdentityInternalTokenCacheEntry
            {
                Token = tokenResponse.AccessToken,
                ExpiresAtUtc = nowUtc.AddSeconds(expiresInSeconds)
            };

            await _tokenCache.SetAsync(
                cacheKey,
                entry,
                options: BuildCacheEntryOptions(nowUtc, entry.ExpiresAtUtc),
                token: cancellationToken);

            return entry.Token;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity permission target validation token request failed.");
            return null;
        }
    }

    private async Task<TokenResponse?> RequestTokenAsync(
        ClientCredentials credentials,
        string scope,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(PermissionIdentityInternalAccessTokenProvider));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{credentials.Authority}/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string?>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = credentials.ClientId,
                ["client_secret"] = credentials.ClientSecret,
                ["scope"] = scope
            })
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Identity permission target validation token request failed. StatusCode={StatusCode}",
                response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
    }

    private ClientCredentials? ResolveClientCredentials()
    {
        var authority = _configuration["AuthServer:Authority"]?.TrimEnd('/');
        var options = _options.Value;
        var clientId = string.IsNullOrWhiteSpace(options.ClientId)
            ? _configuration["OpenIddict:Clients:Internal:ClientId"]
            : options.ClientId.Trim();
        var clientSecret = string.IsNullOrWhiteSpace(options.ClientSecret)
            ? _configuration["OpenIddict:Clients:Internal:Secret"]
            : options.ClientSecret;

        if (string.IsNullOrWhiteSpace(authority) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        return new ClientCredentials(authority, clientId, clientSecret);
    }

    private static bool IsTokenFresh(PermissionIdentityInternalTokenCacheEntry? entry, DateTimeOffset nowUtc)
    {
        return entry != null &&
               !string.IsNullOrWhiteSpace(entry.Token) &&
               entry.ExpiresAtUtc > nowUtc.AddSeconds(30);
    }

    private static string BuildTokenCacheKey(string clientId, string scope)
    {
        return $"permissions:identity-internal:m2m-token:{clientId}:{scope}";
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

    private async Task<PermissionIdentityInternalTokenCacheEntry?> WaitForSharedTokenAsync(
        string cacheKey,
        CancellationToken cancellationToken)
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

    private readonly record struct ClientCredentials(string Authority, string ClientId, string ClientSecret);

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}

[IgnoreCacheMultiTenancy]
public sealed class PermissionIdentityInternalTokenCacheEntry
{
    public string? Token { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
