using Microsoft.Extensions.Caching.Distributed;
using Shared.Caching.Abstractions;

namespace Permissions.Tests.TestDoubles;

internal sealed class InMemoryDistributedCache<TCacheItem> : IDistributedCache<TCacheItem>
    where TCacheItem : class
{
    private readonly Dictionary<string, TCacheItem> _items = new(StringComparer.Ordinal);

    public TCacheItem? Get(string key, bool? hideErrors = null, bool considerUow = false)
        => _items.GetValueOrDefault(key);

    public KeyValuePair<string, TCacheItem?>[] GetMany(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false)
        => keys.Select(key => new KeyValuePair<string, TCacheItem?>(key, Get(key, hideErrors, considerUow))).ToArray();

    public Task<KeyValuePair<string, TCacheItem?>[]> GetManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        => Task.FromResult(GetMany(keys, hideErrors, considerUow));

    public Task<TCacheItem?> GetAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        => Task.FromResult(Get(key, hideErrors, considerUow));

    public TCacheItem? GetOrAdd(string key, Func<TCacheItem> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
    {
        if (_items.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = factory();
        _items[key] = created;
        return created;
    }

    public async Task<TCacheItem?> GetOrAddAsync(string key, Func<Task<TCacheItem>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        if (_items.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = await factory();
        _items[key] = created;
        return created;
    }

    public KeyValuePair<string, TCacheItem?>[] GetOrAddMany(IEnumerable<string> keys, Func<IEnumerable<string>, List<KeyValuePair<string, TCacheItem>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
    {
        var missing = keys.Where(key => !_items.ContainsKey(key)).ToList();
        foreach (var pair in factory(missing))
        {
            _items[pair.Key] = pair.Value;
        }

        return GetMany(keys, hideErrors, considerUow);
    }

    public async Task<KeyValuePair<string, TCacheItem?>[]> GetOrAddManyAsync(IEnumerable<string> keys, Func<IEnumerable<string>, Task<List<KeyValuePair<string, TCacheItem>>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        var missing = keys.Where(key => !_items.ContainsKey(key)).ToList();
        foreach (var pair in await factory(missing))
        {
            _items[pair.Key] = pair.Value;
        }

        return GetMany(keys, hideErrors, considerUow);
    }

    public void Set(string key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
        => _items[key] = value;

    public Task SetAsync(string key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        _items[key] = value;
        return Task.CompletedTask;
    }

    public void SetMany(IEnumerable<KeyValuePair<string, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
    {
        foreach (var pair in items)
        {
            _items[pair.Key] = pair.Value;
        }
    }

    public Task SetManyAsync(IEnumerable<KeyValuePair<string, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        SetMany(items, options, hideErrors, considerUow);
        return Task.CompletedTask;
    }

    public void Refresh(string key, bool? hideErrors = null) { }
    public Task RefreshAsync(string key, bool? hideErrors = null, CancellationToken token = default) => Task.CompletedTask;
    public void RefreshMany(IEnumerable<string> keys, bool? hideErrors = null) { }
    public Task RefreshManyAsync(IEnumerable<string> keys, bool? hideErrors = null, CancellationToken token = default) => Task.CompletedTask;
    public void Remove(string key, bool? hideErrors = null, bool considerUow = false) => _items.Remove(key);

    public Task RemoveAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        _items.Remove(key);
        return Task.CompletedTask;
    }

    public void RemoveMany(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false)
    {
        foreach (var key in keys)
        {
            _items.Remove(key);
        }
    }

    public Task RemoveManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        RemoveMany(keys, hideErrors, considerUow);
        return Task.CompletedTask;
    }
}
