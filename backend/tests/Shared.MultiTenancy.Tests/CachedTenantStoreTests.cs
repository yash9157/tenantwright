using Shared.MultiTenancy.Caching;
using Shared.Caching.Abstractions;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class CachedTenantStoreTests
{
    [Fact]
    public async Task FindAsync_ById_Returns_Tenant_When_Cached()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var byId = new TestDistributedCache<TenantCacheItem>();
        var byName = new TestDistributedCache<TenantByNameCacheItem>();
        var list = new TestDistributedCache<TenantListCacheItem>();
        var stamp = new TestDistributedCache<TenantListStampCacheItem>();

        await byId.SetAsync(tenantId.ToString("D"), new TenantCacheItem
        {
            Id = tenantId,
            Name = "acme",
            NormalizedName = "ACME",
            IsActive = true,
            ConnectionStrings = { ["Default"] = "TenantDb" }
        });

        var store = new CachedTenantStore(byId, byName, list, stamp);

        var tenant = await store.FindAsync(tenantId);

        Assert.NotNull(tenant);
        Assert.Equal(tenantId, tenant!.Id);
        Assert.Equal("acme", tenant.Name);
        Assert.Equal("ACME", tenant.NormalizedName);
        Assert.True(tenant.IsActive);
        Assert.Equal("TenantDb", tenant.ConnectionStrings["Default"]);
    }

    [Fact]
    public async Task FindAsync_ByName_Uses_Name_Lookup_Then_ById()
    {
        var tenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var byId = new TestDistributedCache<TenantCacheItem>();
        var byName = new TestDistributedCache<TenantByNameCacheItem>();
        var list = new TestDistributedCache<TenantListCacheItem>();
        var stamp = new TestDistributedCache<TenantListStampCacheItem>();

        await byName.SetAsync("ACME", new TenantByNameCacheItem { Exists = true, TenantId = tenantId });
        await byId.SetAsync(tenantId.ToString("D"), new TenantCacheItem
        {
            Id = tenantId,
            Name = "acme",
            NormalizedName = "ACME",
            IsActive = true
        });

        var store = new CachedTenantStore(byId, byName, list, stamp);

        var tenant = await store.FindAsync(" acme ");

        Assert.NotNull(tenant);
        Assert.Equal(tenantId, tenant!.Id);
    }

    [Fact]
    public async Task GetListAsync_Returns_List_When_Cached()
    {
        var tenantId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var byId = new TestDistributedCache<TenantCacheItem>();
        var byName = new TestDistributedCache<TenantByNameCacheItem>();
        var list = new TestDistributedCache<TenantListCacheItem>();
        var stamp = new TestDistributedCache<TenantListStampCacheItem>();
        await stamp.SetAsync(TenantCacheConstants.TenantListStampKey, new TenantListStampCacheItem { Stamp = 5 });

        await list.SetAsync(TenantCacheConstants.TenantListKey, new TenantListCacheItem
        {
            Stamp = 5,
            Tenants =
            {
                new TenantCacheItem
                {
                    Id = tenantId,
                    Name = "acme",
                    NormalizedName = "ACME",
                    IsActive = true
                }
            }
        });

        var store = new CachedTenantStore(byId, byName, list, stamp);

        var tenants = await store.GetListAsync();

        Assert.Single(tenants);
        Assert.Equal(tenantId, tenants[0].Id);
    }

    [Fact]
    public async Task GetListAsync_Returns_Empty_When_List_Stamp_Is_Stale()
    {
        var byId = new TestDistributedCache<TenantCacheItem>();
        var byName = new TestDistributedCache<TenantByNameCacheItem>();
        var list = new TestDistributedCache<TenantListCacheItem>();
        var stamp = new TestDistributedCache<TenantListStampCacheItem>();

        await stamp.SetAsync(TenantCacheConstants.TenantListStampKey, new TenantListStampCacheItem { Stamp = 3 });
        await list.SetAsync(TenantCacheConstants.TenantListKey, new TenantListCacheItem
        {
            Stamp = 2,
            Tenants =
            {
                new TenantCacheItem
                {
                    Id = Guid.NewGuid(),
                    Name = "acme",
                    NormalizedName = "ACME",
                    IsActive = true
                }
            }
        });

        var store = new CachedTenantStore(byId, byName, list, stamp);

        var tenants = await store.GetListAsync();

        Assert.Empty(tenants);
    }

    private sealed class TestDistributedCache<TCacheItem> : IDistributedCache<TCacheItem, string>
        where TCacheItem : class
    {
        private readonly Dictionary<string, TCacheItem> _cache = new(StringComparer.Ordinal);

        public TCacheItem? Get(string key, bool? hideErrors = null, bool considerUow = false)
        {
            return _cache.TryGetValue(key, out var value) ? value : null;
        }

        public Task<TCacheItem?> GetAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            return Task.FromResult(Get(key));
        }

        public KeyValuePair<string, TCacheItem?>[] GetMany(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false)
        {
            return keys.Select(k => new KeyValuePair<string, TCacheItem?>(k, Get(k, hideErrors, considerUow))).ToArray();
        }

        public Task<KeyValuePair<string, TCacheItem?>[]> GetManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            return Task.FromResult(GetMany(keys, hideErrors, considerUow));
        }

        public TCacheItem? GetOrAdd(string key, Func<TCacheItem> factory, Func<Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = factory();
            _cache[key] = created;
            return created;
        }

        public async Task<TCacheItem?> GetOrAddAsync(string key, Func<Task<TCacheItem>> factory, Func<Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = await factory();
            _cache[key] = created;
            return created;
        }

        public KeyValuePair<string, TCacheItem?>[] GetOrAddMany(IEnumerable<string> keys, Func<IEnumerable<string>, List<KeyValuePair<string, TCacheItem>>> factory, Func<Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
        {
            var keyArray = keys.ToArray();
            var missing = keyArray.Where(k => !_cache.ContainsKey(k)).ToArray();
            if (missing.Length > 0)
            {
                foreach (var kv in factory(missing))
                {
                    _cache[kv.Key] = kv.Value;
                }
            }

            return keyArray.Select(k => new KeyValuePair<string, TCacheItem?>(k, Get(k, hideErrors, considerUow))).ToArray();
        }

        public async Task<KeyValuePair<string, TCacheItem?>[]> GetOrAddManyAsync(IEnumerable<string> keys, Func<IEnumerable<string>, Task<List<KeyValuePair<string, TCacheItem>>>> factory, Func<Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            var keyArray = keys.ToArray();
            var missing = keyArray.Where(k => !_cache.ContainsKey(k)).ToArray();
            if (missing.Length > 0)
            {
                var created = await factory(missing);
                foreach (var kv in created)
                {
                    _cache[kv.Key] = kv.Value;
                }
            }

            return keyArray.Select(k => new KeyValuePair<string, TCacheItem?>(k, Get(k, hideErrors, considerUow))).ToArray();
        }

        public void Set(string key, TCacheItem value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
        {
            _cache[key] = value;
        }

        public Task SetAsync(string key, TCacheItem value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            Set(key, value, options, hideErrors, considerUow);
            return Task.CompletedTask;
        }

        public void SetMany(IEnumerable<KeyValuePair<string, TCacheItem>> items, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
        {
            foreach (var item in items)
            {
                _cache[item.Key] = item.Value;
            }
        }

        public Task SetManyAsync(IEnumerable<KeyValuePair<string, TCacheItem>> items, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            SetMany(items, options, hideErrors, considerUow);
            return Task.CompletedTask;
        }

        public void Refresh(string key, bool? hideErrors = null)
        {
        }

        public Task RefreshAsync(string key, bool? hideErrors = null, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        public void RefreshMany(IEnumerable<string> keys, bool? hideErrors = null)
        {
        }

        public Task RefreshManyAsync(IEnumerable<string> keys, bool? hideErrors = null, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        public void Remove(string key, bool? hideErrors = null, bool considerUow = false)
        {
            _cache.Remove(key);
        }

        public Task RemoveAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            Remove(key, hideErrors, considerUow);
            return Task.CompletedTask;
        }

        public void RemoveMany(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false)
        {
            foreach (var key in keys)
            {
                _cache.Remove(key);
            }
        }

        public Task RemoveManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            RemoveMany(keys, hideErrors, considerUow);
            return Task.CompletedTask;
        }
    }
}
