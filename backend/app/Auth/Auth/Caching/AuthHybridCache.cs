using Microsoft.Extensions.Caching.Hybrid;
using Shared.Caching.Abstractions;
using Shared.Modularity.DependencyInjection;

namespace Auth.Caching;

public interface IAuthHybridCache<TCacheItem>
    where TCacheItem : class
{
    Task<TCacheItem?> GetAsync(string key, CancellationToken token = default);

    Task SetAsync(
        string key,
        TCacheItem value,
        HybridCacheEntryOptions? options = null,
        CancellationToken token = default);

    Task RemoveAsync(string key, CancellationToken token = default);

    Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken token = default);
}

public sealed class AuthHybridCacheItem<TCacheItem>
    where TCacheItem : class
{
    public bool HasValue { get; set; }

    public TCacheItem? Value { get; set; }
}

public sealed class AuthHybridCache<TCacheItem> : IAuthHybridCache<TCacheItem>, ITransientDependency
    where TCacheItem : class
{
    private static readonly HybridCacheEntryOptions MissingEntryOptions = new()
    {
        LocalCacheExpiration = TimeSpan.FromSeconds(2),
        Expiration = TimeSpan.FromSeconds(2)
    };

    private readonly IHybridCache<AuthHybridCacheItem<TCacheItem>> _cache;

    public AuthHybridCache(IHybridCache<AuthHybridCacheItem<TCacheItem>> cache)
    {
        _cache = cache;
    }

    public async Task<TCacheItem?> GetAsync(string key, CancellationToken token = default)
    {
        var cacheItem = await _cache.GetOrCreateAsync(
            key,
            factory: static () => Task.FromResult(new AuthHybridCacheItem<TCacheItem> { HasValue = false }),
            optionsFactory: static () => MissingEntryOptions,
            considerUow: false,
            token: token);

        if (cacheItem == null || !cacheItem.HasValue)
        {
            return null;
        }

        return cacheItem.Value;
    }

    public Task SetAsync(
        string key,
        TCacheItem value,
        HybridCacheEntryOptions? options = null,
        CancellationToken token = default)
    {
        return _cache.SetAsync(
            key,
            new AuthHybridCacheItem<TCacheItem>
            {
                HasValue = true,
                Value = value
            },
            options: options,
            considerUow: false,
            token: token);
    }

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        return _cache.RemoveAsync(key, considerUow: false, token: token);
    }

    public Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken token = default)
    {
        return _cache.RemoveManyAsync(keys, considerUow: false, token: token);
    }
}
