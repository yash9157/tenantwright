using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Shared.Data.ConnectionStrings;
using Shared.Modularity.DependencyInjection;

namespace Shared.EntityFrameworkCore.ConnectionStrings;

public class TenantConnectionStringsCache : ITenantConnectionStringsCache, ISingletonDependency
{
    private readonly IOptionsMonitor<SharedConnectionStringResolverOptions> _options;

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    public TenantConnectionStringsCache(IOptionsMonitor<SharedConnectionStringResolverOptions> options)
    {
        _options = options;
    }

    public async Task<TenantConnectionStringsCacheItem> GetOrAddAsync(
        Guid tenantId,
        Func<Task<TenantConnectionStringsCacheItem>> factory,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(tenantId, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Item;
        }

        var item = await factory();
        if (item is not { Exists: true })
        {
            // Unknown tenants fail closed at resolution and are not pinned in-process: a cold or flushed tenant cache
            // looks identical to an unknown tenant, so the next lookup must see the repopulated entry immediately
            // instead of failing for another TenantCacheSeconds. The tenant store negative-caches the miss itself.
            _cache.TryRemove(tenantId, out _);
            return item ?? new TenantConnectionStringsCacheItem { Exists = false };
        }

        _cache[tenantId] = new CacheEntry(item, DateTime.UtcNow.Add(GetCacheDuration()));
        return item;
    }

    public Task RemoveAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        _cache.TryRemove(tenantId, out _);
        return Task.CompletedTask;
    }

    private TimeSpan GetCacheDuration()
    {
        return TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.TenantCacheSeconds));
    }

    private sealed record CacheEntry(TenantConnectionStringsCacheItem Item, DateTime ExpiresAtUtc);
}
