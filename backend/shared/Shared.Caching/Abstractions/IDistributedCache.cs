using Microsoft.Extensions.Caching.Distributed;

namespace Shared.Caching.Abstractions;

public interface IDistributedCache
{
    byte[]? Get(string key);

    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    byte[]?[] GetMany(IEnumerable<string> keys);

    Task<byte[]?[]> GetManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default);

    void Set(string key, byte[] value, DistributedCacheEntryOptions options);

    Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken cancellationToken = default);

    void SetMany(IEnumerable<KeyValuePair<string, byte[]>> items, DistributedCacheEntryOptions options);

    Task SetManyAsync(
        IEnumerable<KeyValuePair<string, byte[]>> items,
        DistributedCacheEntryOptions options,
        CancellationToken cancellationToken = default);

    void Refresh(string key);

    Task RefreshAsync(string key, CancellationToken cancellationToken = default);

    void RefreshMany(IEnumerable<string> keys);

    Task RefreshManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default);

    void Remove(string key);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    void RemoveMany(IEnumerable<string> keys);

    Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default);
}

public interface IDistributedCache<TCacheItem> : IDistributedCache<TCacheItem, string>
    where TCacheItem : class
{
}

public interface IDistributedCache<TCacheItem, TCacheKey>
    where TCacheItem : class
{
    TCacheItem? Get(TCacheKey key, bool? hideErrors = null, bool considerUow = false);

    KeyValuePair<TCacheKey, TCacheItem?>[] GetMany(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false);

    Task<KeyValuePair<TCacheKey, TCacheItem?>[]> GetManyAsync(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default);

    Task<TCacheItem?> GetAsync(TCacheKey key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default);

    TCacheItem? GetOrAdd(TCacheKey key, Func<TCacheItem> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false);

    Task<TCacheItem?> GetOrAddAsync(TCacheKey key, Func<Task<TCacheItem>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default);

    KeyValuePair<TCacheKey, TCacheItem?>[] GetOrAddMany(IEnumerable<TCacheKey> keys, Func<IEnumerable<TCacheKey>, List<KeyValuePair<TCacheKey, TCacheItem>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false);

    Task<KeyValuePair<TCacheKey, TCacheItem?>[]> GetOrAddManyAsync(IEnumerable<TCacheKey> keys, Func<IEnumerable<TCacheKey>, Task<List<KeyValuePair<TCacheKey, TCacheItem>>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default);

    void Set(TCacheKey key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false);

    Task SetAsync(TCacheKey key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default);

    void SetMany(IEnumerable<KeyValuePair<TCacheKey, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false);

    Task SetManyAsync(IEnumerable<KeyValuePair<TCacheKey, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default);

    void Refresh(TCacheKey key, bool? hideErrors = null);

    Task RefreshAsync(TCacheKey key, bool? hideErrors = null, CancellationToken token = default);

    void RefreshMany(IEnumerable<TCacheKey> keys, bool? hideErrors = null);

    Task RefreshManyAsync(IEnumerable<TCacheKey> keys, bool? hideErrors = null, CancellationToken token = default);

    void Remove(TCacheKey key, bool? hideErrors = null, bool considerUow = false);

    Task RemoveAsync(TCacheKey key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default);

    void RemoveMany(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false);

    Task RemoveManyAsync(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default);
}
