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

namespace Shared.Permissions.Features;

public interface IRemoteFeatureAccessTokenProvider
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class RemoteFeatureAccessTokenProvider : IRemoteFeatureAccessTokenProvider, ISingletonDependency
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<FeatureCheckerClientOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly IDistributedCache<RemoteFeatureAccessTokenCacheItem, string> _tokenCache;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<RemoteFeatureAccessTokenProvider> _logger;

    public RemoteFeatureAccessTokenProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<FeatureCheckerClientOptions> options,
        IConfiguration configuration,
        IDistributedCache<RemoteFeatureAccessTokenCacheItem, string> tokenCache,
        IDistributedLock distributedLock,
        ILogger<RemoteFeatureAccessTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _configuration = configuration;
        _tokenCache = tokenCache;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var authority = _configuration["AuthServer:Authority"]?.TrimEnd('/');
            var options = _options.Value;
            var scope = string.IsNullOrWhiteSpace(options.Scope) ? "features.check" : options.Scope.Trim();
            var clientId = string.IsNullOrWhiteSpace(options.ClientId)
                ? _configuration["OpenIddict:Clients:Internal:ClientId"]
                : options.ClientId.Trim();
            var clientSecret = string.IsNullOrWhiteSpace(options.ClientSecret)
                ? _configuration["OpenIddict:Clients:Internal:Secret"]
                : options.ClientSecret;

            if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogWarning("Remote feature checker token configuration is incomplete.");
                return null;
            }

            if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
            {
                _logger.LogWarning("AuthServer:Authority is invalid for remote feature checker token flow.");
                return null;
            }

            var cacheKey = BuildTokenCacheKey(authorityUri, clientId, scope);
            var existing = await _tokenCache.GetAsync(cacheKey, token: cancellationToken);
            if (IsTokenFresh(existing, now))
            {
                return existing!.Token;
            }

            await using var refreshLock = await _distributedLock.TryAcquireAsync(
                $"permissions:feature-check-token:{scope}",
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

            var client = _httpClientFactory.CreateClient(nameof(RemoteFeatureAccessTokenProvider));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(authorityUri, "/connect/token"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string?>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["scope"] = scope
                })
            };

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Remote feature checker token request failed. Status: {StatusCode}", response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<AccessTokenResponse>(cancellationToken: cancellationToken);
            if (payload == null || string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                _logger.LogWarning("Remote feature checker token response is missing access_token.");
                return null;
            }

            var expiresInSeconds = payload.ExpiresIn > 0 ? payload.ExpiresIn : 60;
            var entry = new RemoteFeatureAccessTokenCacheItem
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote feature checker token retrieval failed.");
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

    private static bool IsTokenFresh(RemoteFeatureAccessTokenCacheItem? entry, DateTimeOffset nowUtc)
    {
        return entry != null &&
               !string.IsNullOrWhiteSpace(entry.Token) &&
               entry.ExpiresAtUtc > nowUtc.AddSeconds(30);
    }

    private static string BuildTokenCacheKey(Uri authorityUri, string clientId, string scope)
    {
        return $"permissions:m2m:{authorityUri.Host}:{clientId}:{scope}";
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

    private async Task<RemoteFeatureAccessTokenCacheItem?> WaitForSharedTokenAsync(string cacheKey, CancellationToken cancellationToken)
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
public sealed class RemoteFeatureAccessTokenCacheItem
{
    public string? Token { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
