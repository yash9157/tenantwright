using System.Net.Http.Json;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Permissions.Application.Contracts.Features;
using Shared.Caching.Abstractions;
using Shared.Modularity.DependencyInjection;

namespace Shared.Permissions.Features;

[DisableConventionalRegistration]
public class HttpFeatureCheckerClient : IFeatureCheckerClient, ITransientDependency
{
    private static readonly HybridCacheEntryOptions FeatureCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(60)
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<FeatureCheckerClientOptions> _options;
    private readonly IHybridCache<FeatureValueCacheItem, string> _cache;
    private readonly ILogger<HttpFeatureCheckerClient> _logger;
    private readonly IRemoteFeatureAccessTokenProvider _accessTokenProvider;

    public HttpFeatureCheckerClient(
        IHttpClientFactory httpClientFactory,
        IOptions<FeatureCheckerClientOptions> options,
        IHybridCache<FeatureValueCacheItem, string> cache,
        ILogger<HttpFeatureCheckerClient> logger,
        IRemoteFeatureAccessTokenProvider accessTokenProvider)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _cache = cache;
        _logger = logger;
        _accessTokenProvider = accessTokenProvider;
    }

    public async Task<bool> IsEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken = default)
    {
        var map = await AreEnabledAsync(tenantId, new[] { featureName }, cancellationToken);
        return map.TryGetValue(featureName.Trim(), out var enabled) && enabled;
    }

    public async Task<Dictionary<string, bool>> AreEnabledAsync(Guid? tenantId, IReadOnlyCollection<string> featureNames, CancellationToken cancellationToken = default)
    {
        var normalized = featureNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        var options = _options.Value;
        if (!options.Enabled)
        {
            return BuildDefaultResult(normalized, options.FailOpen);
        }

        if (string.IsNullOrWhiteSpace(options.BaseAddress))
        {
            _logger.LogWarning("Remote feature checker is enabled but BaseAddress is missing. Returning default value with FailOpen={FailOpen}.", options.FailOpen);
            return BuildDefaultResult(normalized, options.FailOpen);
        }

        var remoteEval = new Lazy<Task<Dictionary<string, bool>>>(() => FetchAsync(options, tenantId, normalized, cancellationToken), isThreadSafe: true);

        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var featureName in normalized)
        {
            var cacheKey = BuildCacheKey(tenantId, featureName);
            var cacheItem = await _cache.GetOrCreateAsync(
                cacheKey,
                async () =>
                {
                    var map = await remoteEval.Value;
                    var enabled = map.TryGetValue(featureName, out var v) && v;
                    return new FeatureValueCacheItem { Enabled = enabled, UpdatedAtUtc = DateTimeOffset.UtcNow };
                },
                optionsFactory: () => FeatureCacheOptions,
                considerUow: false,
                token: cancellationToken);

            result[featureName] = cacheItem?.Enabled ?? options.FailOpen;
        }

        return result;
    }

    private static Dictionary<string, bool> BuildDefaultResult(IEnumerable<string> featureNames, bool enabled)
    {
        return featureNames.ToDictionary(x => x, _ => enabled, StringComparer.Ordinal);
    }

    private static string BuildCacheKey(Guid? tenantId, string featureName)
    {
        var tenantPart = tenantId.HasValue ? tenantId.Value.ToString("D") : "host";
        return $"t:{tenantPart}:f:{featureName}";
    }

    public sealed class FeatureValueCacheItem
    {
        public bool Enabled { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class BatchFeatureCheckRequest
    {
        public Guid? TenantId { get; set; }
        public List<string> FeatureNames { get; set; } = new();
    }

    private sealed class BatchFeatureCheckResponse
    {
        public Dictionary<string, bool> Results { get; set; } = new(StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, bool>> FetchAsync(
        FeatureCheckerClientOptions options,
        Guid? tenantId,
        IReadOnlyList<string> featureNames,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var baseAddress))
        {
            _logger.LogWarning("Remote feature checker BaseAddress is invalid. Returning default value with FailOpen={FailOpen}. BaseAddress={BaseAddress}", options.FailOpen, options.BaseAddress);
            return BuildDefaultResult(featureNames, options.FailOpen);
        }

        var client = _httpClientFactory.CreateClient(nameof(HttpFeatureCheckerClient));
        client.BaseAddress = baseAddress;
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 2);

        var request = new BatchFeatureCheckRequest
        {
            TenantId = tenantId,
            FeatureNames = featureNames.ToList()
        };

        try
        {
            var token = await _accessTokenProvider.GetAccessTokenAsync(cancellationToken);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/permissions/feature-check/batch")
            {
                Content = JsonContent.Create(request)
            };

            if (!string.IsNullOrWhiteSpace(token))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<BatchFeatureCheckResponse>(cancellationToken: cancellationToken);
            return payload?.Results ?? BuildDefaultResult(featureNames, options.FailOpen);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote feature check failed. Returning default value with FailOpen={FailOpen}.", options.FailOpen);
            return BuildDefaultResult(featureNames, options.FailOpen);
        }
    }
}
