using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;

namespace Shared.Caching;

public sealed class SharedDistributedCacheOptions
{
    public bool HideErrors { get; set; } = true;

    public string KeyPrefix { get; set; } = string.Empty;

    public DistributedCacheEntryOptions GlobalCacheEntryOptions { get; set; } = new();

    public List<Func<string, DistributedCacheEntryOptions?>> CacheConfigurators { get; } = [];

    public List<Func<string, bool?>> HideErrorConfigurators { get; } = [];

    public void ConfigureCache<TCacheItem>(DistributedCacheEntryOptions? options)
    {
        ConfigureCache(typeof(TCacheItem), options);
    }

    public void ConfigureCache(Type cacheItemType, DistributedCacheEntryOptions? options)
    {
        ConfigureCache(CacheNameAttribute.GetCacheName(cacheItemType), options);
    }

    public void ConfigureCache(string cacheName, DistributedCacheEntryOptions? options)
    {
        CacheConfigurators.Add(name =>
            string.Equals(name, cacheName, StringComparison.Ordinal)
                ? options
                : null);
    }

    public void ConfigureHideErrors<TCacheItem>(bool? hideErrors)
    {
        ConfigureHideErrors(CacheNameAttribute.GetCacheName(typeof(TCacheItem)), hideErrors);
    }

    public void ConfigureHideErrors(string cacheName, bool? hideErrors)
    {
        HideErrorConfigurators.Add(name =>
            string.Equals(name, cacheName, StringComparison.Ordinal)
                ? hideErrors
                : null);
    }
}

public sealed class SharedHybridCacheOptions
{
    public bool HideErrors { get; set; } = true;

    public string KeyPrefix { get; set; } = string.Empty;

    public HybridCacheEntryOptions GlobalHybridCacheEntryOptions { get; set; } = new();

    public List<Func<string, HybridCacheEntryOptions?>> CacheConfigurators { get; } = [];

    public List<Func<string, bool?>> HideErrorConfigurators { get; } = [];

    public void ConfigureCache<TCacheItem>(HybridCacheEntryOptions? options)
    {
        ConfigureCache(typeof(TCacheItem), options);
    }

    public void ConfigureCache(Type cacheItemType, HybridCacheEntryOptions? options)
    {
        ConfigureCache(CacheNameAttribute.GetCacheName(cacheItemType), options);
    }

    public void ConfigureCache(string cacheName, HybridCacheEntryOptions? options)
    {
        CacheConfigurators.Add(name =>
            string.Equals(name, cacheName, StringComparison.Ordinal)
                ? options
                : null);
    }

    public void ConfigureHideErrors<TCacheItem>(bool? hideErrors)
    {
        ConfigureHideErrors(CacheNameAttribute.GetCacheName(typeof(TCacheItem)), hideErrors);
    }

    public void ConfigureHideErrors(string cacheName, bool? hideErrors)
    {
        HideErrorConfigurators.Add(name =>
            string.Equals(name, cacheName, StringComparison.Ordinal)
                ? hideErrors
                : null);
    }
}
