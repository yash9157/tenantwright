using System.Security.Cryptography;
using System.Text;
using Auth.Caching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;

namespace Auth.TokenMetadata;

public sealed class TokenMetadataCacheService :
    ITokenMetadataReadService,
    ITokenMetadataInvalidationService,
    ITokenMetadataWarmupService,
    ISingletonDependency
{
    private readonly IOpenIddictTokenMetadataSource _source;
    private readonly ITokenMetadataStampStore _stampStore;
    private readonly IAuthHybridCache<TokenScopeSetResourcesCacheItem> _scopeSetCache;
    private readonly IAuthHybridCache<TokenScopeResourcesCacheItem> _scopeCache;
    private readonly IAuthHybridCache<TokenClientMetadataCacheItem> _clientCache;
    private readonly TokenMetadataCacheOptions _options;
    private readonly ILogger<TokenMetadataCacheService> _logger;

    public TokenMetadataCacheService(
        IOpenIddictTokenMetadataSource source,
        ITokenMetadataStampStore stampStore,
        IAuthHybridCache<TokenScopeSetResourcesCacheItem> scopeSetCache,
        IAuthHybridCache<TokenScopeResourcesCacheItem> scopeCache,
        IAuthHybridCache<TokenClientMetadataCacheItem> clientCache,
        IOptions<TokenMetadataCacheOptions> options,
        ILogger<TokenMetadataCacheService> logger)
    {
        _source = source;
        _stampStore = stampStore;
        _scopeSetCache = scopeSetCache;
        _scopeCache = scopeCache;
        _clientCache = clientCache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetResourcesForScopesAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default)
    {
        var normalizedScopes = NormalizeScopes(scopes);
        if (normalizedScopes.Length == 0)
        {
            return [];
        }

        if (!_options.Enabled)
        {
            return await ResolveResourcesDirectlyAsync(normalizedScopes, cancellationToken);
        }

        var stamp = await _stampStore.GetCurrentStampAsync(cancellationToken);
        return await GetResourcesForScopeSetAsync(normalizedScopes, stamp, cancellationToken);
    }

    public async Task<TokenClientMetadataCacheItem?> GetClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var normalizedClientId = clientId.Trim();

        if (!_options.Enabled)
        {
            return await _source.GetClientAsync(normalizedClientId, cancellationToken);
        }

        var stamp = await _stampStore.GetCurrentStampAsync(cancellationToken);
        var cacheKey = BuildClientCacheKey(stamp, normalizedClientId);
        var cachedClient = await _clientCache.GetAsync(cacheKey, cancellationToken);
        if (cachedClient != null)
        {
            return cachedClient;
        }

        var resolvedClient = await _source.GetClientAsync(normalizedClientId, cancellationToken);
        if (resolvedClient == null)
        {
            return null;
        }

        await _clientCache.SetAsync(
            cacheKey,
            resolvedClient,
            options: CreateOptions(_options.ClientL1Seconds, _options.ClientL2Minutes),
            token: cancellationToken);

        return resolvedClient;
    }

    public async Task InvalidateClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !_options.Enabled)
        {
            return;
        }

        var normalizedClientId = clientId.Trim();
        var stamp = await _stampStore.GetCurrentStampAsync(cancellationToken);
        var clientCacheKey = BuildClientCacheKey(stamp, normalizedClientId);

        await _clientCache.RemoveAsync(clientCacheKey, cancellationToken);
        await BumpStampAndLogAsync("application", normalizedClientId, cancellationToken);
    }

    public async Task InvalidateScopeAsync(string scopeName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopeName) || !_options.Enabled)
        {
            return;
        }

        var normalizedScope = scopeName.Trim();
        var stamp = await _stampStore.GetCurrentStampAsync(cancellationToken);
        var scopeCacheKey = BuildScopeCacheKey(stamp, normalizedScope);

        await _scopeCache.RemoveAsync(scopeCacheKey, cancellationToken);
        await BumpStampAndLogAsync("scope", normalizedScope, cancellationToken);
    }

    public async Task InvalidateAllAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        await BumpStampAndLogAsync("all", reason, cancellationToken);
    }

    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Token metadata cache warm-up skipped because cache is disabled.");
            return;
        }

        var stamp = await _stampStore.GetCurrentStampAsync(cancellationToken);

        var clients = 0;
        await foreach (var clientId in _source.ListClientIdsAsync(cancellationToken))
        {
            var client = await GetClientAsync(clientId, cancellationToken);
            if (client != null)
            {
                clients++;
            }
        }

        var scopes = 0;
        await foreach (var scopeName in _source.ListScopeNamesAsync(cancellationToken))
        {
            await GetScopeResourcesAsync(scopeName, stamp, cancellationToken);
            scopes++;
        }

        _logger.LogInformation(
            "Token metadata cache warm-up completed. Clients={ClientCount}, Scopes={ScopeCount}.",
            clients,
            scopes);
    }

    public async Task WarmupCommonScopeSetsAsync(
        IEnumerable<IEnumerable<string>> scopeSets,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || scopeSets == null)
        {
            return;
        }

        var stamp = await _stampStore.GetCurrentStampAsync(cancellationToken);
        var warmedCount = 0;

        foreach (var scopeSet in scopeSets)
        {
            var normalized = NormalizeScopes(scopeSet);
            if (normalized.Length == 0)
            {
                continue;
            }

            await GetResourcesForScopeSetAsync(normalized, stamp, cancellationToken);
            warmedCount++;
        }

        _logger.LogInformation(
            "Token metadata common scope-set warm-up completed. ScopeSets={ScopeSetCount}.",
            warmedCount);
    }

    private async Task<string[]> GetScopeResourcesAsync(string scope, string stamp, CancellationToken cancellationToken)
    {
        var cacheKey = BuildScopeCacheKey(stamp, scope);
        var cachedScope = await _scopeCache.GetAsync(cacheKey, cancellationToken);
        if (cachedScope != null)
        {
            return cachedScope.Resources;
        }

        var resources = await _source.GetScopeResourcesAsync(scope, cancellationToken);
        var cacheItem = new TokenScopeResourcesCacheItem
        {
            ScopeName = scope,
            Resources = resources
        };

        await _scopeCache.SetAsync(
            cacheKey,
            cacheItem,
            options: CreateOptions(_options.ScopeResourceL1Seconds, _options.ScopeResourceL2Minutes),
            token: cancellationToken);

        return resources;
    }

    private async Task<IReadOnlyList<string>> GetResourcesForScopeSetAsync(
        string[] normalizedScopes,
        string stamp,
        CancellationToken cancellationToken)
    {
        var scopeSetHash = HashScopeSet(normalizedScopes);
        var scopeSetKey = BuildScopeSetCacheKey(stamp, scopeSetHash);
        var cachedScopeSet = await _scopeSetCache.GetAsync(scopeSetKey, cancellationToken);
        if (cachedScopeSet != null)
        {
            return cachedScopeSet.Resources;
        }

        var merged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in normalizedScopes)
        {
            var resources = await GetScopeResourcesAsync(scope, stamp, cancellationToken);
            foreach (var resource in resources)
            {
                if (!string.IsNullOrWhiteSpace(resource))
                {
                    merged.Add(resource);
                }
            }
        }

        var combinedResources = merged.ToArray();
        await _scopeSetCache.SetAsync(
            scopeSetKey,
            new TokenScopeSetResourcesCacheItem
            {
                ScopeSetHash = scopeSetHash,
                ScopeNames = normalizedScopes,
                Resources = combinedResources
            },
            options: CreateOptions(_options.ScopeSetL1Seconds, _options.ScopeSetL2Minutes),
            token: cancellationToken);

        return combinedResources;
    }

    private async Task<IReadOnlyList<string>> ResolveResourcesDirectlyAsync(IEnumerable<string> normalizedScopes, CancellationToken cancellationToken)
    {
        var merged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in normalizedScopes)
        {
            var resources = await _source.GetScopeResourcesAsync(scope, cancellationToken);
            foreach (var resource in resources)
            {
                if (!string.IsNullOrWhiteSpace(resource))
                {
                    merged.Add(resource);
                }
            }
        }

        return [.. merged];
    }

    private async Task BumpStampAndLogAsync(string entityType, string entityIdentifier, CancellationToken cancellationToken)
    {
        await _stampStore.BumpStampAsync(cancellationToken);

        _logger.LogInformation(
            "Token metadata cache invalidated. EntityType={EntityType}, EntityHash={EntityHash}",
            entityType,
            HashIdentifier(entityIdentifier));
    }

    private string BuildScopeCacheKey(string stamp, string scope)
        => $"{_options.KeyPrefix}:Scope:{stamp}:{Uri.EscapeDataString(scope)}";

    private string BuildScopeSetCacheKey(string stamp, string scopeSetHash)
        => $"{_options.KeyPrefix}:ScopeSet:{stamp}:{scopeSetHash}";

    private string BuildClientCacheKey(string stamp, string clientId)
        => $"{_options.KeyPrefix}:Client:{stamp}:{Uri.EscapeDataString(clientId)}";

    private static HybridCacheEntryOptions CreateOptions(int localSeconds, int distributedMinutes)
    {
        return new HybridCacheEntryOptions
        {
            LocalCacheExpiration = TimeSpan.FromSeconds(Math.Max(1, localSeconds)),
            Expiration = TimeSpan.FromMinutes(Math.Max(1, distributedMinutes))
        };
    }

    private static string[] NormalizeScopes(IEnumerable<string> scopes)
    {
        if (scopes == null)
        {
            return [];
        }

        return scopes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }

    private static string HashIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes[..6]).ToLowerInvariant();
    }

    private static string HashScopeSet(IEnumerable<string> scopeNames)
    {
        var joined = string.Join("|", scopeNames);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
