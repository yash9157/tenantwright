using System;
using System.Threading;
using System.Threading.Tasks;
using Auth.Models;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;

namespace Auth.Caching;

public interface IOpenIddictApplicationCache
{
    Task<OpenIddictApplicationModel?> GetByClientIdAsync(string clientId, CancellationToken token = default);
    Task SetAsync(OpenIddictApplicationModel application, CancellationToken token = default);
    Task RemoveAsync(string clientId, CancellationToken token = default);
}

public class OpenIddictApplicationCache : IOpenIddictApplicationCache, ITransientDependency
{
    private readonly IAuthHybridCache<OpenIddictApplicationModel> _cache;
    private readonly OpenIddictCacheOptions _options;
    private const string CachePrefix = "OpenIddict:Application:";

    public OpenIddictApplicationCache(
        IAuthHybridCache<OpenIddictApplicationModel> cache,
        IOptions<OpenIddictCacheOptions> options)
    {
        _cache = cache;
        _options = options.Value;
    }

    public async Task<OpenIddictApplicationModel?> GetByClientIdAsync(string clientId, CancellationToken token = default)
    {
        return await _cache.GetAsync(CachePrefix + clientId, token);
    }

    public async Task SetAsync(OpenIddictApplicationModel application, CancellationToken token = default)
    {
        if (application.ClientId != null)
        {
            await _cache.SetAsync(
                CachePrefix + application.ClientId,
                application,
                options: CreateOptions(_options.ApplicationTtlMinutes),
                token: token);
        }
    }

    public async Task RemoveAsync(string clientId, CancellationToken token = default)
    {
        await _cache.RemoveAsync(CachePrefix + clientId, token);
    }

    private static HybridCacheEntryOptions CreateOptions(int ttlMinutes)
    {
        var ttl = TimeSpan.FromMinutes(Math.Max(1, ttlMinutes));
        return new HybridCacheEntryOptions
        {
            LocalCacheExpiration = ttl,
            Expiration = ttl
        };
    }
}

public interface IOpenIddictScopeCache
{
    Task<OpenIddictScopeModel?> GetByNameAsync(string name, CancellationToken token = default);
    Task SetAsync(OpenIddictScopeModel scope, CancellationToken token = default);
    Task RemoveAsync(string name, CancellationToken token = default);
}

public class OpenIddictScopeCache : IOpenIddictScopeCache, ITransientDependency
{
    private readonly IAuthHybridCache<OpenIddictScopeModel> _cache;
    private readonly OpenIddictCacheOptions _options;
    private const string CachePrefix = "OpenIddict:Scope:";

    public OpenIddictScopeCache(
        IAuthHybridCache<OpenIddictScopeModel> cache,
        IOptions<OpenIddictCacheOptions> options)
    {
        _cache = cache;
        _options = options.Value;
    }

    public async Task<OpenIddictScopeModel?> GetByNameAsync(string name, CancellationToken token = default)
    {
        return await _cache.GetAsync(CachePrefix + name, token);
    }

    public async Task SetAsync(OpenIddictScopeModel scope, CancellationToken token = default)
    {
        if (scope.Name != null)
        {
            await _cache.SetAsync(
                CachePrefix + scope.Name,
                scope,
                options: CreateOptions(_options.ScopeTtlMinutes),
                token: token);
        }
    }

    public async Task RemoveAsync(string name, CancellationToken token = default)
    {
        await _cache.RemoveAsync(CachePrefix + name, token);
    }

    private static HybridCacheEntryOptions CreateOptions(int ttlMinutes)
    {
        var ttl = TimeSpan.FromMinutes(Math.Max(1, ttlMinutes));
        return new HybridCacheEntryOptions
        {
            LocalCacheExpiration = ttl,
            Expiration = ttl
        };
    }
}
