using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shared.Caching;
using Shared.Caching.Abstractions;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;

namespace Auth.Clients;

public interface IInternalAccessTokenProvider
{
    Task<string?> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default);

    Task<string?> GetAccessTokenAsync(string scope, Guid tenantId, CancellationToken cancellationToken = default);
}

public class InternalAccessTokenProvider : IInternalAccessTokenProvider
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IDistributedCache<InternalAccessTokenCacheEntry, string> _tokenCache;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<InternalAccessTokenProvider> _logger;

    public InternalAccessTokenProvider(
        HttpClient httpClient,
        IConfiguration configuration,
        IDistributedCache<InternalAccessTokenCacheEntry, string> tokenCache,
        IDistributedLock distributedLock,
        ILogger<InternalAccessTokenProvider> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _tokenCache = tokenCache;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    public async Task<string?> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default)
        => await GetAccessTokenCoreAsync(scope, tenantId: null, cancellationToken);

    public async Task<string?> GetAccessTokenAsync(string scope, Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            return null;
        }

        return await GetAccessTokenCoreAsync(scope, tenantId, cancellationToken);
    }

    private async Task<string?> GetAccessTokenCoreAsync(string scope, Guid? tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var cacheKey = BuildTokenCacheKey(scope, tenantId);
            var existing = await _tokenCache.GetAsync(cacheKey, token: cancellationToken);
            if (IsTokenFresh(existing, nowUtc))
            {
                return existing!.Token;
            }

            await using var refreshLock = await _distributedLock.TryAcquireAsync(
                $"auth:m2m-token:{scope}:{tenantId?.ToString("N") ?? "host"}",
                new DistributedLockAcquireOptions
                {
                    PolicyName = DistributedLockPolicyNames.M2mTokenAcquisition,
                    IgnoreTenant = true
                },
                cancellationToken);

            if (refreshLock == null)
            {
                var sharedToken = await WaitForSharedTokenAsync(cacheKey, cancellationToken);
                if (sharedToken != null)
                {
                    return sharedToken.Token;
                }

                return null;
            }

            nowUtc = DateTimeOffset.UtcNow;
            existing = await _tokenCache.GetAsync(cacheKey, token: cancellationToken);
            if (IsTokenFresh(existing, nowUtc))
            {
                return existing!.Token;
            }

            var tokenResponse = await RequestTokenAsync(scope, tenantId, cancellationToken);
            if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken) || tokenResponse.ExpiresIn is null or <= 0)
            {
                return null;
            }

            var entry = new InternalAccessTokenCacheEntry
            {
                Token = tokenResponse.AccessToken,
                ExpiresAtUtc = nowUtc.AddSeconds(tokenResponse.ExpiresIn.Value)
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
            _logger.LogWarning(ex, "Internal access token retrieval failed.");
            return null;
        }
    }

    private async Task<TokenResponse?> RequestTokenAsync(string scope, Guid? tenantId, CancellationToken cancellationToken)
    {
        var authority = _configuration["AuthServer:Authority"]?.TrimEnd('/');
        var clientId = _configuration["OpenIddict:Clients:Internal:ClientId"];
        var clientSecret = _configuration["OpenIddict:Clients:Internal:Secret"];

        if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Internal token client configuration is missing.");
        }

        var tokenEndpoint = $"{authority}/connect/token";
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string?>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = scope,
                ["tenant_id"] = tenantId?.ToString("D")
            })
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Internal access token request failed. Status: {StatusCode}", response.StatusCode);
            throw new InvalidOperationException($"Internal token request failed with status code {(int)response.StatusCode}.");
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
        if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            _logger.LogWarning("Internal access token response is missing access_token.");
            throw new InvalidOperationException("Internal token response is missing access_token.");
        }

        return tokenResponse;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }
    }

    private static bool IsTokenFresh(InternalAccessTokenCacheEntry? entry, DateTimeOffset nowUtc)
    {
        return entry != null &&
               !string.IsNullOrWhiteSpace(entry.Token) &&
               entry.ExpiresAtUtc > nowUtc.AddSeconds(30);
    }

    private static string BuildTokenCacheKey(string scope, Guid? tenantId)
    {
        return $"m2m:token:{scope.Trim()}:{tenantId?.ToString("D") ?? "host"}";
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

    private async Task<InternalAccessTokenCacheEntry?> WaitForSharedTokenAsync(string cacheKey, CancellationToken cancellationToken)
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
public sealed class InternalAccessTokenCacheEntry
{
    public string? Token { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
