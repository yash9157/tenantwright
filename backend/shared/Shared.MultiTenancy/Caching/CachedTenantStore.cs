using Shared.Modularity.DependencyInjection;
using Shared.Caching.Abstractions;
using Microsoft.Extensions.Caching.Distributed;

namespace Shared.MultiTenancy.Caching;

public class CachedTenantStore : ITenantStore, ISingletonDependency
{
    private static readonly DistributedCacheEntryOptions NegativeCacheOptions =
        new()
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        };

    private readonly IDistributedCache<TenantCacheItem, string> _tenantByIdCache;
    private readonly IDistributedCache<TenantByNameCacheItem, string> _tenantByNameCache;
    private readonly IDistributedCache<TenantListCacheItem, string> _tenantListCache;
    private readonly IDistributedCache<TenantListStampCacheItem, string> _tenantListStampCache;

    public CachedTenantStore(
        IDistributedCache<TenantCacheItem, string> tenantByIdCache,
        IDistributedCache<TenantByNameCacheItem, string> tenantByNameCache,
        IDistributedCache<TenantListCacheItem, string> tenantListCache,
        IDistributedCache<TenantListStampCacheItem, string> tenantListStampCache)
    {
        _tenantByIdCache = tenantByIdCache;
        _tenantByNameCache = tenantByNameCache;
        _tenantListCache = tenantListCache;
        _tenantListStampCache = tenantListStampCache;
    }

    public TenantConfiguration? Find(Guid id)
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        var item = _tenantByIdCache.GetOrAdd(
            id.ToString("D"),
            () => new TenantCacheItem
            {
                Exists = false,
                Id = id
            },
            optionsFactory: () => NegativeCacheOptions);

        if (item == null || !item.Exists)
        {
            TenantResolutionMetrics.RecordStoreCacheOperation(TenantByIdCache.Name, "miss");
            return null;
        }

        TenantResolutionMetrics.RecordStoreCacheOperation(TenantByIdCache.Name, "hit");
        return Map(item);
    }

    public TenantConfiguration? Find(string normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        var key = normalizedName.Trim().ToUpperInvariant();
        var lookup = _tenantByNameCache.GetOrAdd(
            key,
            () => new TenantByNameCacheItem { Exists = false },
            optionsFactory: () => NegativeCacheOptions);

        if (lookup == null || !lookup.Exists)
        {
            TenantResolutionMetrics.RecordStoreCacheOperation(TenantByNameCache.Name, "miss");
            return null;
        }

        if (lookup.Tenant != null && lookup.Tenant.Exists)
        {
            TenantResolutionMetrics.RecordStoreCacheOperation(TenantByNameCache.Name, "hit");
            return Map(lookup.Tenant);
        }

        TenantResolutionMetrics.RecordStoreCacheOperation(TenantByNameCache.Name, "partial_hit");
        return lookup.TenantId == Guid.Empty ? null : Find(lookup.TenantId);
    }

    public IReadOnlyList<TenantConfiguration> GetList()
    {
        var stamp = _tenantListStampCache.Get(TenantCacheConstants.TenantListStampKey);
        var list = _tenantListCache.Get(TenantCacheConstants.TenantListKey);
        if (list == null || list.Tenants.Count == 0)
        {
            TenantResolutionMetrics.RecordStoreCacheOperation(TenantListCache.Name, "miss");
            return Array.Empty<TenantConfiguration>();
        }

        if (stamp != null && list.Stamp != stamp.Stamp)
        {
            TenantResolutionMetrics.RecordStoreCacheOperation(TenantListCache.Name, "stale");
            return Array.Empty<TenantConfiguration>();
        }

        TenantResolutionMetrics.RecordStoreCacheOperation(TenantListCache.Name, "hit");
        return list.Tenants.Where(item => item.Exists).Select(Map).ToList();
    }

    public async Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        var item = await _tenantByIdCache.GetOrAddAsync(
            id.ToString("D"),
            () => Task.FromResult(new TenantCacheItem
            {
                Exists = false,
                Id = id
            }),
            optionsFactory: () => NegativeCacheOptions,
            token: cancellationToken);

        if (item == null || !item.Exists)
        {
            TenantResolutionMetrics.RecordStoreCacheOperation(TenantByIdCache.Name, "miss");
            return null;
        }

        TenantResolutionMetrics.RecordStoreCacheOperation(TenantByIdCache.Name, "hit");
        return Map(item);
    }

    public async Task<TenantConfiguration?> FindAsync(string normalizedName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        var key = normalizedName.Trim().ToUpperInvariant();
        var lookup = await _tenantByNameCache.GetOrAddAsync(
            key,
            () => Task.FromResult(new TenantByNameCacheItem { Exists = false }),
            optionsFactory: () => NegativeCacheOptions,
            token: cancellationToken);

        if (lookup == null || !lookup.Exists)
        {
            TenantResolutionMetrics.RecordStoreCacheOperation(TenantByNameCache.Name, "miss");
            return null;
        }

        if (lookup.Tenant != null && lookup.Tenant.Exists)
        {
            TenantResolutionMetrics.RecordStoreCacheOperation(TenantByNameCache.Name, "hit");
            return Map(lookup.Tenant);
        }

        TenantResolutionMetrics.RecordStoreCacheOperation(TenantByNameCache.Name, "partial_hit");
        return lookup.TenantId == Guid.Empty
            ? null
            : await FindAsync(lookup.TenantId, cancellationToken);
    }

    public async Task<IReadOnlyList<TenantConfiguration>> GetListAsync(CancellationToken cancellationToken = default)
    {
        var list = await FindListAsync(cancellationToken);
        return list is { Count: > 0 } ? list : Array.Empty<TenantConfiguration>();
    }

    public async Task<IReadOnlyList<TenantConfiguration>?> FindListAsync(CancellationToken cancellationToken = default)
    {
        var stamp = await _tenantListStampCache.GetAsync(TenantCacheConstants.TenantListStampKey, token: cancellationToken);
        var list = await _tenantListCache.GetAsync(TenantCacheConstants.TenantListKey, token: cancellationToken);
        if (list == null)
        {
            TenantResolutionMetrics.RecordStoreCacheOperation(TenantListCache.Name, "miss");
            return null;
        }

        if (stamp != null && list.Stamp != stamp.Stamp)
        {
            TenantResolutionMetrics.RecordStoreCacheOperation(TenantListCache.Name, "stale");
            return null;
        }

        TenantResolutionMetrics.RecordStoreCacheOperation(TenantListCache.Name, "hit");
        return list.Tenants.Where(item => item.Exists).Select(Map).ToList();
    }

    private static TenantConfiguration Map(TenantCacheItem item)
    {
        return new TenantConfiguration
        {
            Id = item.Id,
            Name = item.Name,
            NormalizedName = item.NormalizedName,
            IsActive = item.IsActive,
            ConnectionStrings = new Dictionary<string, string>(item.ConnectionStrings, StringComparer.Ordinal),
            DedicatedDatabaseNames = item.DedicatedDatabaseNames
                .Concat(item.ConnectionStrings.Keys)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
        };
    }
}
