using Microsoft.Extensions.Caching.Distributed;
using Shared.Caching.Abstractions;

namespace Shared.Caching.Implementations;

public abstract class DistributedCacheBase : Shared.Caching.Abstractions.IDistributedCache
{
    public abstract byte[]? Get(string key);

    public abstract Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    public virtual byte[]?[] GetMany(IEnumerable<string> keys)
    {
        var keyArray = keys as string[] ?? keys.ToArray();
        var results = new byte[]?[keyArray.Length];

        for (var i = 0; i < keyArray.Length; i++)
        {
            results[i] = Get(keyArray[i]);
        }

        return results;
    }

    public virtual async Task<byte[]?[]> GetManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var keyArray = keys as string[] ?? keys.ToArray();
        var tasks = new Task<byte[]?>[keyArray.Length];

        for (var i = 0; i < keyArray.Length; i++)
        {
            tasks[i] = GetAsync(keyArray[i], cancellationToken);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public abstract void Set(string key, byte[] value, DistributedCacheEntryOptions options);

    public abstract Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken cancellationToken = default);

    public virtual void SetMany(IEnumerable<KeyValuePair<string, byte[]>> items, DistributedCacheEntryOptions options)
    {
        var itemArray = items as KeyValuePair<string, byte[]>[] ?? items.ToArray();
        for (var i = 0; i < itemArray.Length; i++)
        {
            Set(itemArray[i].Key, itemArray[i].Value, options);
        }
    }

    public virtual async Task SetManyAsync(
        IEnumerable<KeyValuePair<string, byte[]>> items,
        DistributedCacheEntryOptions options,
        CancellationToken cancellationToken = default)
    {
        var itemArray = items as KeyValuePair<string, byte[]>[] ?? items.ToArray();
        var tasks = new Task[itemArray.Length];

        for (var i = 0; i < itemArray.Length; i++)
        {
            tasks[i] = SetAsync(itemArray[i].Key, itemArray[i].Value, options, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public abstract void Refresh(string key);

    public virtual Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public virtual void RefreshMany(IEnumerable<string> keys)
    {
        var keyArray = keys as string[] ?? keys.ToArray();
        for (var i = 0; i < keyArray.Length; i++)
        {
            Refresh(keyArray[i]);
        }
    }

    public virtual async Task RefreshManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var keyArray = keys as string[] ?? keys.ToArray();
        var tasks = new Task[keyArray.Length];

        for (var i = 0; i < keyArray.Length; i++)
        {
            tasks[i] = RefreshAsync(keyArray[i], cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public abstract void Remove(string key);

    public abstract Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    public virtual void RemoveMany(IEnumerable<string> keys)
    {
        var keyArray = keys as string[] ?? keys.ToArray();
        for (var i = 0; i < keyArray.Length; i++)
        {
            Remove(keyArray[i]);
        }
    }

    public virtual async Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var keyArray = keys as string[] ?? keys.ToArray();
        var tasks = new Task[keyArray.Length];

        for (var i = 0; i < keyArray.Length; i++)
        {
            tasks[i] = RemoveAsync(keyArray[i], cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
