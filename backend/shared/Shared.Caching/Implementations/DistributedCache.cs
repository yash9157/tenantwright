using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Caching.Abstractions;
using Shared.Threading;
using Shared.Uow.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Caching.Implementations;

public class DistributedCache<TCacheItem> : IDistributedCache<TCacheItem>
    where TCacheItem : class
{
    public IDistributedCache<TCacheItem, string> InternalCache { get; }

    public DistributedCache(IDistributedCache<TCacheItem, string> internalCache)
    {
        InternalCache = internalCache;
    }

    public TCacheItem? Get(string key, bool? hideErrors = null, bool considerUow = false)
    {
        return InternalCache.Get(key, hideErrors, considerUow);
    }

    public KeyValuePair<string, TCacheItem?>[] GetMany(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false)
    {
        return InternalCache.GetMany(keys, hideErrors, considerUow);
    }

    public Task<KeyValuePair<string, TCacheItem?>[]> GetManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        return InternalCache.GetManyAsync(keys, hideErrors, considerUow, token);
    }

    public Task<TCacheItem?> GetAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        return InternalCache.GetAsync(key, hideErrors, considerUow, token);
    }

    public TCacheItem? GetOrAdd(string key, Func<TCacheItem> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
    {
        return InternalCache.GetOrAdd(key, factory, optionsFactory, hideErrors, considerUow);
    }

    public Task<TCacheItem?> GetOrAddAsync(string key, Func<Task<TCacheItem>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        return InternalCache.GetOrAddAsync(key, factory, optionsFactory, hideErrors, considerUow, token);
    }

    public KeyValuePair<string, TCacheItem?>[] GetOrAddMany(IEnumerable<string> keys, Func<IEnumerable<string>, List<KeyValuePair<string, TCacheItem>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
    {
        return InternalCache.GetOrAddMany(keys, factory, optionsFactory, hideErrors, considerUow);
    }

    public Task<KeyValuePair<string, TCacheItem?>[]> GetOrAddManyAsync(IEnumerable<string> keys, Func<IEnumerable<string>, Task<List<KeyValuePair<string, TCacheItem>>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        return InternalCache.GetOrAddManyAsync(keys, factory, optionsFactory, hideErrors, considerUow, token);
    }

    public void Set(string key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
    {
        InternalCache.Set(key, value, options, hideErrors, considerUow);
    }

    public Task SetAsync(string key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        return InternalCache.SetAsync(key, value, options, hideErrors, considerUow, token);
    }

    public void SetMany(IEnumerable<KeyValuePair<string, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
    {
        InternalCache.SetMany(items, options, hideErrors, considerUow);
    }

    public Task SetManyAsync(IEnumerable<KeyValuePair<string, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        return InternalCache.SetManyAsync(items, options, hideErrors, considerUow, token);
    }

    public void Refresh(string key, bool? hideErrors = null)
    {
        InternalCache.Refresh(key, hideErrors);
    }

    public Task RefreshAsync(string key, bool? hideErrors = null, CancellationToken token = default)
    {
        return InternalCache.RefreshAsync(key, hideErrors, token);
    }

    public void RefreshMany(IEnumerable<string> keys, bool? hideErrors = null)
    {
        InternalCache.RefreshMany(keys, hideErrors);
    }

    public Task RefreshManyAsync(IEnumerable<string> keys, bool? hideErrors = null, CancellationToken token = default)
    {
        return InternalCache.RefreshManyAsync(keys, hideErrors, token);
    }

    public void Remove(string key, bool? hideErrors = null, bool considerUow = false)
    {
        InternalCache.Remove(key, hideErrors, considerUow);
    }

    public Task RemoveAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        return InternalCache.RemoveAsync(key, hideErrors, considerUow, token);
    }

    public void RemoveMany(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false)
    {
        InternalCache.RemoveMany(keys, hideErrors, considerUow);
    }

    public Task RemoveManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        return InternalCache.RemoveManyAsync(keys, hideErrors, considerUow, token);
    }
}

public class DistributedCache<TCacheItem, TCacheKey> : IDistributedCache<TCacheItem, TCacheKey>
    where TCacheItem : class
    where TCacheKey : notnull
{
    public const string UowCacheName = "SharedDistributedCache";

    public ILogger<DistributedCache<TCacheItem, TCacheKey>> Logger { get; set; }

    protected string CacheName { get; set; } = default!;

    protected bool IgnoreMultiTenancy { get; set; }

    protected Microsoft.Extensions.Caching.Distributed.IDistributedCache Cache { get; }

    protected ICancellationTokenProvider CancellationTokenProvider { get; }

    protected IDistributedCacheSerializer Serializer { get; }

    protected IDistributedCacheKeyNormalizer KeyNormalizer { get; }

    protected IServiceScopeFactory ServiceScopeFactory { get; }

    protected IUnitOfWorkManager UnitOfWorkManager { get; }

    protected SemaphoreSlim SyncSemaphore { get; }

    protected DistributedCacheEntryOptions DefaultCacheOptions { get; set; } = default!;

    private readonly SharedDistributedCacheOptions _distributedCacheOption;

    public DistributedCache(
        IOptions<SharedDistributedCacheOptions> distributedCacheOption,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache cache,
        ICancellationTokenProvider cancellationTokenProvider,
        IDistributedCacheSerializer serializer,
        IDistributedCacheKeyNormalizer keyNormalizer,
        IServiceScopeFactory serviceScopeFactory,
        IUnitOfWorkManager unitOfWorkManager)
    {
        _distributedCacheOption = distributedCacheOption.Value;
        Cache = cache;
        CancellationTokenProvider = cancellationTokenProvider;
        Logger = NullLogger<DistributedCache<TCacheItem, TCacheKey>>.Instance;
        Serializer = serializer;
        KeyNormalizer = keyNormalizer;
        ServiceScopeFactory = serviceScopeFactory;
        UnitOfWorkManager = unitOfWorkManager;

        SyncSemaphore = new SemaphoreSlim(1, 1);

        SetDefaultOptions();
    }

    protected virtual string NormalizeKey(TCacheKey key)
    {
        return KeyNormalizer.NormalizeKey(
            new DistributedCacheKeyNormalizeArgs(
                key.ToString()!,
                CacheName,
                IgnoreMultiTenancy
            )
        );
    }

    protected virtual DistributedCacheEntryOptions GetDefaultCacheEntryOptions()
    {
        foreach (var configure in _distributedCacheOption.CacheConfigurators)
        {
            var options = configure.Invoke(CacheName);
            if (options != null)
            {
                return options;
            }
        }

        return _distributedCacheOption.GlobalCacheEntryOptions;
    }

    protected virtual bool ResolveHideErrors(bool? hideErrors)
    {
        if (hideErrors.HasValue)
        {
            return hideErrors.Value;
        }

        foreach (var configure in _distributedCacheOption.HideErrorConfigurators)
        {
            var configuredHideErrors = configure.Invoke(CacheName);
            if (configuredHideErrors.HasValue)
            {
                return configuredHideErrors.Value;
            }
        }

        return _distributedCacheOption.HideErrors;
    }

    protected virtual void SetDefaultOptions()
    {
        CacheName = CacheNameAttribute.GetCacheName(typeof(TCacheItem));
        IgnoreMultiTenancy = typeof(TCacheItem).IsDefined(typeof(IgnoreCacheMultiTenancyAttribute), true);

        // Configure default cache entry options
        DefaultCacheOptions = GetDefaultCacheEntryOptions();
    }

    public virtual TCacheItem? Get(TCacheKey key, bool? hideErrors = null, bool considerUow = false)
    {
        hideErrors = ResolveHideErrors(hideErrors);

        if (ShouldConsiderUow(considerUow))
        {
            var value = GetUnitOfWorkCache().GetValueOrDefault(key)?.GetUnRemovedValueOrNull();
            if (value != null)
            {
                return value;
            }
        }

        byte[]? cachedBytes;

        try
        {
            cachedBytes = Cache.Get(NormalizeKey(key));
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                HandleException(ex);
                return null;
            }

            throw;
        }

        return ToCacheItem(cachedBytes);
    }

    public virtual KeyValuePair<TCacheKey, TCacheItem?>[] GetMany(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false)
    {
        var keyArray = keys.ToArray();

        var cacheSupportsMultipleItems = Cache as ICacheSupportsMultipleItems;
        if (cacheSupportsMultipleItems == null)
        {
            return GetManyFallback(keyArray, hideErrors, considerUow);
        }

        var notCachedKeys = new List<TCacheKey>();
        var cachedValues = new List<KeyValuePair<TCacheKey, TCacheItem?>>();
        if (ShouldConsiderUow(considerUow))
        {
            var uowCache = GetUnitOfWorkCache();
            foreach (var key in keyArray)
            {
                var value = uowCache.GetValueOrDefault(key)?.GetUnRemovedValueOrNull();
                if (value != null)
                {
                    cachedValues.Add(new KeyValuePair<TCacheKey, TCacheItem?>(key, value));
                }
            }

            notCachedKeys = keyArray.Except(cachedValues.Select(x => x.Key)).ToList();
            if (notCachedKeys.Count == 0)
            {
                return cachedValues.ToArray();
            }
        }

        hideErrors = ResolveHideErrors(hideErrors);
        byte[]?[] cachedBytes;

        var readKeys = notCachedKeys.Count != 0 ? notCachedKeys.ToArray() : keyArray;
        try
        {
            cachedBytes = cacheSupportsMultipleItems.GetMany(readKeys.Select(NormalizeKey));
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                HandleException(ex);
                return ToCacheItemsWithDefaultValues(keyArray);
            }

            throw;
        }

        return cachedValues.Concat(ToCacheItems(cachedBytes, readKeys)).ToArray();
    }

    protected virtual KeyValuePair<TCacheKey, TCacheItem?>[] GetManyFallback(TCacheKey[] keys, bool? hideErrors = null, bool considerUow = false)
    {
        hideErrors = ResolveHideErrors(hideErrors);

        try
        {
            return keys.Select(key => new KeyValuePair<TCacheKey, TCacheItem?>(key, Get(key, false, considerUow))).ToArray();
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                HandleException(ex);
                return ToCacheItemsWithDefaultValues(keys);
            }

            throw;
        }
    }

    public virtual async Task<KeyValuePair<TCacheKey, TCacheItem?>[]> GetManyAsync(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        var keyArray = keys.ToArray();

        var cacheSupportsMultipleItems = Cache as ICacheSupportsMultipleItems;
        if (cacheSupportsMultipleItems == null)
        {
            return await GetManyFallbackAsync(keyArray, hideErrors, considerUow, token);
        }

        var notCachedKeys = new List<TCacheKey>();
        var cachedValues = new List<KeyValuePair<TCacheKey, TCacheItem?>>();
        if (ShouldConsiderUow(considerUow))
        {
            var uowCache = GetUnitOfWorkCache();
            foreach (var key in keyArray)
            {
                var value = uowCache.GetValueOrDefault(key)?.GetUnRemovedValueOrNull();
                if (value != null)
                {
                    cachedValues.Add(new KeyValuePair<TCacheKey, TCacheItem?>(key, value));
                }
            }

            notCachedKeys = keyArray.Except(cachedValues.Select(x => x.Key)).ToList();
            if (notCachedKeys.Count == 0)
            {
                return cachedValues.ToArray();
            }
        }

        hideErrors = ResolveHideErrors(hideErrors);
        byte[]?[] cachedBytes;

        var readKeys = notCachedKeys.Count != 0 ? notCachedKeys.ToArray() : keyArray;

        try
        {
            cachedBytes = await cacheSupportsMultipleItems.GetManyAsync(
                readKeys.Select(NormalizeKey),
                CancellationTokenProvider.FallbackToProvider(token)
            );
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                await HandleExceptionAsync(ex);
                return ToCacheItemsWithDefaultValues(keyArray);
            }

            throw;
        }

        return cachedValues.Concat(ToCacheItems(cachedBytes, readKeys)).ToArray();
    }

    protected virtual async Task<KeyValuePair<TCacheKey, TCacheItem?>[]> GetManyFallbackAsync(TCacheKey[] keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        hideErrors = ResolveHideErrors(hideErrors);

        try
        {
            var result = new List<KeyValuePair<TCacheKey, TCacheItem?>>();

            foreach (var key in keys)
            {
                result.Add(new KeyValuePair<TCacheKey, TCacheItem?>(
                    key,
                    await GetAsync(key, false, considerUow, token: token))
                );
            }

            return result.ToArray();
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                await HandleExceptionAsync(ex);
                return ToCacheItemsWithDefaultValues(keys);
            }

            throw;
        }
    }

    public virtual async Task<TCacheItem?> GetAsync(TCacheKey key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        hideErrors = ResolveHideErrors(hideErrors);

        if (ShouldConsiderUow(considerUow))
        {
            var value = GetUnitOfWorkCache().GetValueOrDefault(key)?.GetUnRemovedValueOrNull();
            if (value != null)
            {
                return value;
            }
        }

        byte[]? cachedBytes;

        try
        {
            cachedBytes = await Cache.GetAsync(
                NormalizeKey(key),
                CancellationTokenProvider.FallbackToProvider(token)
            );
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                await HandleExceptionAsync(ex);
                return null;
            }

            throw;
        }

        if (cachedBytes == null)
        {
            return null;
        }

        return Serializer.Deserialize<TCacheItem>(cachedBytes);
    }

    public virtual TCacheItem? GetOrAdd(TCacheKey key, Func<TCacheItem> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
    {
        var value = Get(key, hideErrors, considerUow);
        if (value != null)
        {
            return value;
        }

        using (SyncSemaphore.Lock())
        {
            value = Get(key, hideErrors, considerUow);
            if (value != null)
            {
                return value;
            }

            value = factory();

            if (ShouldConsiderUow(considerUow))
            {
                var uowCache = GetUnitOfWorkCache();
                if (uowCache.TryGetValue(key, out var item))
                {
                    item.SetValue(value);
                }
                else
                {
                    uowCache.Add(key, new UnitOfWorkCacheItem<TCacheItem>(value));
                }
            }

            Set(key, value, optionsFactory?.Invoke(), hideErrors, considerUow);
        }

        return value;
    }

    public virtual async Task<TCacheItem?> GetOrAddAsync(TCacheKey key, Func<Task<TCacheItem>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        token = CancellationTokenProvider.FallbackToProvider(token);
        var value = await GetAsync(key, hideErrors, considerUow, token);
        if (value != null)
        {
            return value;
        }

        using (await SyncSemaphore.LockAsync(token))
        {
            value = await GetAsync(key, hideErrors, considerUow, token);
            if (value != null)
            {
                return value;
            }

            value = await factory();

            if (ShouldConsiderUow(considerUow))
            {
                var uowCache = GetUnitOfWorkCache();
                if (uowCache.TryGetValue(key, out var item))
                {
                    item.SetValue(value);
                }
                else
                {
                    uowCache.Add(key, new UnitOfWorkCacheItem<TCacheItem>(value));
                }
            }

            await SetAsync(key, value, optionsFactory?.Invoke(), hideErrors, considerUow, token);
        }

        return value;
    }

    public KeyValuePair<TCacheKey, TCacheItem?>[] GetOrAddMany(IEnumerable<TCacheKey> keys, Func<IEnumerable<TCacheKey>, List<KeyValuePair<TCacheKey, TCacheItem>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false)
    {
        KeyValuePair<TCacheKey, TCacheItem?>[] result;
        var keyArray = keys.ToArray();

        var cacheSupportsMultipleItems = Cache as ICacheSupportsMultipleItems;
        if (cacheSupportsMultipleItems == null)
        {
            result = GetManyFallback(keyArray, hideErrors, considerUow);
        }
        else
        {
            var notCachedKeys = new List<TCacheKey>();
            var cachedValues = new List<KeyValuePair<TCacheKey, TCacheItem?>>();
            if (ShouldConsiderUow(considerUow))
            {
                var uowCache = GetUnitOfWorkCache();
                foreach (var key in keyArray)
                {
                    var value = uowCache.GetValueOrDefault(key)?.GetUnRemovedValueOrNull();
                    if (value != null)
                    {
                        cachedValues.Add(new KeyValuePair<TCacheKey, TCacheItem?>(key, value));
                    }
                }

                notCachedKeys = keyArray.Except(cachedValues.Select(x => x.Key)).ToList();
                if (notCachedKeys.Count == 0)
                {
                    return cachedValues.ToArray();
                }
            }

            hideErrors = ResolveHideErrors(hideErrors);
            byte[]?[] cachedBytes;

            var readKeys = notCachedKeys.Count != 0 ? notCachedKeys.ToArray() : keyArray;
            try
            {
                cachedBytes = cacheSupportsMultipleItems.GetMany(readKeys.Select(NormalizeKey));
            }
            catch (Exception ex)
            {
                if (hideErrors == true)
                {
                    HandleException(ex);
                    return ToCacheItemsWithDefaultValues(keyArray);
                }

                throw;
            }

            result = cachedValues.Concat(ToCacheItems(cachedBytes, readKeys)).ToArray();
        }

        if (result.All(x => x.Value != null))
        {
            return result!;
        }

        var missingKeys = new List<TCacheKey>();
        var missingValuesIndex = new List<int>();
        for (var i = 0; i < keyArray.Length; i++)
        {
            if (result[i].Value != null)
            {
                continue;
            }

            missingKeys.Add(keyArray[i]);
            missingValuesIndex.Add(i);
        }

        var missingValues = factory.Invoke(missingKeys).ToArray();
        var valueQueue = new Queue<KeyValuePair<TCacheKey, TCacheItem>>(missingValues);

        SetMany(missingValues, optionsFactory?.Invoke(), hideErrors, considerUow);

        foreach (var index in missingValuesIndex)
        {
            result[index] = valueQueue.Dequeue()!;
        }

        return result;
    }

    public async Task<KeyValuePair<TCacheKey, TCacheItem?>[]> GetOrAddManyAsync(IEnumerable<TCacheKey> keys, Func<IEnumerable<TCacheKey>, Task<List<KeyValuePair<TCacheKey, TCacheItem>>>> factory, Func<DistributedCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        KeyValuePair<TCacheKey, TCacheItem?>[] result;
        var keyArray = keys.ToArray();

        var cacheSupportsMultipleItems = Cache as ICacheSupportsMultipleItems;
        if (cacheSupportsMultipleItems == null)
        {
            result = await GetManyFallbackAsync(keyArray, hideErrors, considerUow, token);
        }
        else
        {
            var notCachedKeys = new List<TCacheKey>();
            var cachedValues = new List<KeyValuePair<TCacheKey, TCacheItem?>>();
            if (ShouldConsiderUow(considerUow))
            {
                var uowCache = GetUnitOfWorkCache();
                foreach (var key in keyArray)
                {
                    var value = uowCache.GetValueOrDefault(key)?.GetUnRemovedValueOrNull();
                    if (value != null)
                    {
                        cachedValues.Add(new KeyValuePair<TCacheKey, TCacheItem?>(key, value));
                    }
                }

                notCachedKeys = keyArray.Except(cachedValues.Select(x => x.Key)).ToList();
                if (notCachedKeys.Count == 0)
                {
                    return cachedValues.ToArray();
                }
            }

            hideErrors = ResolveHideErrors(hideErrors);
            byte[]?[] cachedBytes;

            var readKeys = notCachedKeys.Count != 0 ? notCachedKeys.ToArray() : keyArray;
            try
            {
                cachedBytes = await cacheSupportsMultipleItems.GetManyAsync(readKeys.Select(NormalizeKey), token);
            }
            catch (Exception ex)
            {
                if (hideErrors == true)
                {
                    await HandleExceptionAsync(ex);
                    return ToCacheItemsWithDefaultValues(keyArray);
                }

                throw;
            }

            result = cachedValues.Concat(ToCacheItems(cachedBytes, readKeys)).ToArray();
        }

        if (result.All(x => x.Value != null))
        {
            return result;
        }

        var missingKeys = new List<TCacheKey>();
        var missingValuesIndex = new List<int>();
        for (var i = 0; i < keyArray.Length; i++)
        {
            if (result[i].Value != null)
            {
                continue;
            }

            missingKeys.Add(keyArray[i]);
            missingValuesIndex.Add(i);
        }

        var missingValues = (await factory.Invoke(missingKeys)).ToArray();
        var valueQueue = new Queue<KeyValuePair<TCacheKey, TCacheItem>>(missingValues);

        await SetManyAsync(missingValues, optionsFactory?.Invoke(), hideErrors, considerUow, token);

        foreach (var index in missingValuesIndex)
        {
            result[index] = valueQueue.Dequeue()!;
        }

        return result;
    }

    public virtual void Set(TCacheKey key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
    {
        void SetRealCache()
        {
            hideErrors = ResolveHideErrors(hideErrors);

            try
            {
                Cache.Set(NormalizeKey(key), Serializer.Serialize(value), options ?? GetDefaultCacheEntryOptions());
            }
            catch (Exception ex)
            {
                if (hideErrors == true)
                {
                    HandleException(ex);
                    return;
                }

                throw;
            }
        }

        if (ShouldConsiderUow(considerUow))
        {
            var uowCache = GetUnitOfWorkCache();
            if (uowCache.TryGetValue(key, out _))
            {
                uowCache[key].SetValue(value);
            }
            else
            {
                uowCache.Add(key, new UnitOfWorkCacheItem<TCacheItem>(value));
            }

            UnitOfWorkManager.Current?.OnCompleted(() =>
            {
                SetRealCache();
                return Task.CompletedTask;
            });
        }
        else
        {
            SetRealCache();
        }
    }

    public virtual async Task SetAsync(TCacheKey key, TCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        async Task SetRealCache()
        {
            hideErrors = ResolveHideErrors(hideErrors);

            try
            {
                await Cache.SetAsync(
                    NormalizeKey(key),
                    Serializer.Serialize(value),
                    options ?? GetDefaultCacheEntryOptions(),
                    CancellationTokenProvider.FallbackToProvider(token)
                );
            }
            catch (Exception ex)
            {
                if (hideErrors == true)
                {
                    await HandleExceptionAsync(ex);
                    return;
                }

                throw;
            }
        }

        if (ShouldConsiderUow(considerUow))
        {
            var uowCache = GetUnitOfWorkCache();
            if (uowCache.TryGetValue(key, out _))
            {
                uowCache[key].SetValue(value);
            }
            else
            {
                uowCache.Add(key, new UnitOfWorkCacheItem<TCacheItem>(value));
            }

            UnitOfWorkManager.Current?.OnCompleted(SetRealCache);
        }
        else
        {
            await SetRealCache();
        }
    }

    public void SetMany(IEnumerable<KeyValuePair<TCacheKey, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
    {
        var itemsArray = items.ToArray();

        var cacheSupportsMultipleItems = Cache as ICacheSupportsMultipleItems;
        if (cacheSupportsMultipleItems == null)
        {
            SetManyFallback(itemsArray, options, hideErrors, considerUow);
            return;
        }

        void SetRealCache()
        {
            hideErrors = ResolveHideErrors(hideErrors);

            try
            {
                cacheSupportsMultipleItems.SetMany(ToRawCacheItems(itemsArray), options ?? GetDefaultCacheEntryOptions());
            }
            catch (Exception ex)
            {
                if (hideErrors == true)
                {
                    HandleException(ex);
                    return;
                }

                throw;
            }
        }

        if (ShouldConsiderUow(considerUow))
        {
            var uowCache = GetUnitOfWorkCache();

            foreach (var pair in itemsArray)
            {
                if (uowCache.TryGetValue(pair.Key, out _))
                {
                    uowCache[pair.Key].SetValue(pair.Value);
                }
                else
                {
                    uowCache.Add(pair.Key, new UnitOfWorkCacheItem<TCacheItem>(pair.Value));
                }
            }

            UnitOfWorkManager.Current?.OnCompleted(() =>
            {
                SetRealCache();
                return Task.CompletedTask;
            });
        }
        else
        {
            SetRealCache();
        }
    }

    protected virtual void SetManyFallback(KeyValuePair<TCacheKey, TCacheItem>[] items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
    {
        hideErrors = ResolveHideErrors(hideErrors);

        try
        {
            foreach (var item in items)
            {
                Set(item.Key, item.Value, options, false, considerUow);
            }
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                HandleException(ex);
                return;
            }

            throw;
        }
    }

    public virtual async Task SetManyAsync(IEnumerable<KeyValuePair<TCacheKey, TCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        var itemsArray = items.ToArray();

        var cacheSupportsMultipleItems = Cache as ICacheSupportsMultipleItems;
        if (cacheSupportsMultipleItems == null)
        {
            await SetManyFallbackAsync(itemsArray, options, hideErrors, considerUow, token);
            return;
        }

        async Task SetRealCache()
        {
            hideErrors = ResolveHideErrors(hideErrors);

            try
            {
                await cacheSupportsMultipleItems.SetManyAsync(
                    ToRawCacheItems(itemsArray),
                    options ?? GetDefaultCacheEntryOptions(),
                    CancellationTokenProvider.FallbackToProvider(token)
                );
            }
            catch (Exception ex)
            {
                if (hideErrors == true)
                {
                    await HandleExceptionAsync(ex);
                    return;
                }

                throw;
            }
        }

        if (ShouldConsiderUow(considerUow))
        {
            var uowCache = GetUnitOfWorkCache();

            foreach (var pair in itemsArray)
            {
                if (uowCache.TryGetValue(pair.Key, out _))
                {
                    uowCache[pair.Key].SetValue(pair.Value);
                }
                else
                {
                    uowCache.Add(pair.Key, new UnitOfWorkCacheItem<TCacheItem>(pair.Value));
                }
            }

            UnitOfWorkManager.Current?.OnCompleted(SetRealCache);
        }
        else
        {
            await SetRealCache();
        }
    }

    protected virtual async Task SetManyFallbackAsync(KeyValuePair<TCacheKey, TCacheItem>[] items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        hideErrors = ResolveHideErrors(hideErrors);

        try
        {
            foreach (var item in items)
            {
                await SetAsync(item.Key, item.Value, options, false, considerUow, token: token);
            }
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                await HandleExceptionAsync(ex);
                return;
            }

            throw;
        }
    }

    public virtual void Refresh(TCacheKey key, bool? hideErrors = null)
    {
        hideErrors = ResolveHideErrors(hideErrors);

        try
        {
            Cache.Refresh(NormalizeKey(key));
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                HandleException(ex);
                return;
            }

            throw;
        }
    }

    public virtual async Task RefreshAsync(TCacheKey key, bool? hideErrors = null, CancellationToken token = default)
    {
        hideErrors = ResolveHideErrors(hideErrors);

        try
        {
            await Cache.RefreshAsync(NormalizeKey(key), CancellationTokenProvider.FallbackToProvider(token));
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                await HandleExceptionAsync(ex);
                return;
            }

            throw;
        }
    }

    public virtual void RefreshMany(IEnumerable<TCacheKey> keys, bool? hideErrors = null)
    {
        hideErrors = ResolveHideErrors(hideErrors);

        try
        {
            if (Cache is ICacheSupportsMultipleItems cacheSupportsMultipleItems)
            {
                cacheSupportsMultipleItems.RefreshMany(keys.Select(NormalizeKey));
            }
            else
            {
                foreach (var key in keys)
                {
                    Cache.Refresh(NormalizeKey(key));
                }
            }
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                HandleException(ex);
                return;
            }

            throw;
        }
    }

    public virtual async Task RefreshManyAsync(IEnumerable<TCacheKey> keys, bool? hideErrors = null, CancellationToken token = default)
    {
        hideErrors = ResolveHideErrors(hideErrors);

        try
        {
            if (Cache is ICacheSupportsMultipleItems cacheSupportsMultipleItems)
            {
                await cacheSupportsMultipleItems.RefreshManyAsync(keys.Select(NormalizeKey), token);
            }
            else
            {
                foreach (var key in keys)
                {
                    await Cache.RefreshAsync(NormalizeKey(key), token);
                }
            }
        }
        catch (Exception ex)
        {
            if (hideErrors == true)
            {
                await HandleExceptionAsync(ex);
                return;
            }

            throw;
        }
    }

    public virtual void Remove(TCacheKey key, bool? hideErrors = null, bool considerUow = false)
    {
        void RemoveRealCache()
        {
            hideErrors = ResolveHideErrors(hideErrors);

            try
            {
                Cache.Remove(NormalizeKey(key));
            }
            catch (Exception ex)
            {
                if (hideErrors == true)
                {
                    HandleException(ex);
                    return;
                }

                throw;
            }
        }

        if (ShouldConsiderUow(considerUow))
        {
            var uowCache = GetUnitOfWorkCache();
            if (uowCache.TryGetValue(key, out _))
            {
                uowCache[key].RemoveValue();
            }

            UnitOfWorkManager.Current?.OnCompleted(() =>
            {
                RemoveRealCache();
                return Task.CompletedTask;
            });
        }
        else
        {
            RemoveRealCache();
        }
    }

    public virtual async Task RemoveAsync(TCacheKey key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        async Task RemoveRealCache()
        {
            hideErrors = ResolveHideErrors(hideErrors);

            try
            {
                await Cache.RemoveAsync(NormalizeKey(key), CancellationTokenProvider.FallbackToProvider(token));
            }
            catch (Exception ex)
            {
                if (hideErrors == true)
                {
                    await HandleExceptionAsync(ex);
                    return;
                }

                throw;
            }
        }

        if (ShouldConsiderUow(considerUow))
        {
            var uowCache = GetUnitOfWorkCache();
            if (uowCache.TryGetValue(key, out _))
            {
                uowCache[key].RemoveValue();
            }

            UnitOfWorkManager.Current?.OnCompleted(RemoveRealCache);
        }
        else
        {
            await RemoveRealCache();
        }
    }

    public void RemoveMany(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false)
    {
        var keyArray = keys.ToArray();

        if (Cache is ICacheSupportsMultipleItems cacheSupportsMultipleItems)
        {
            void RemoveRealCache()
            {
                hideErrors = ResolveHideErrors(hideErrors);

                try
                {
                    cacheSupportsMultipleItems.RemoveMany(keyArray.Select(NormalizeKey));
                }
                catch (Exception ex)
                {
                    if (hideErrors == true)
                    {
                        HandleException(ex);
                        return;
                    }

                    throw;
                }
            }

            if (ShouldConsiderUow(considerUow))
            {
                var uowCache = GetUnitOfWorkCache();

                foreach (var key in keyArray)
                {
                    if (uowCache.TryGetValue(key, out _))
                    {
                        uowCache[key].RemoveValue();
                    }
                }

                UnitOfWorkManager.Current?.OnCompleted(() =>
                {
                    RemoveRealCache();
                    return Task.CompletedTask;
                });
            }
            else
            {
                RemoveRealCache();
            }
        }
        else
        {
            foreach (var key in keyArray)
            {
                Remove(key, hideErrors, considerUow);
            }
        }
    }

    public async Task RemoveManyAsync(IEnumerable<TCacheKey> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        var keyArray = keys.ToArray();

        if (Cache is ICacheSupportsMultipleItems cacheSupportsMultipleItems)
        {
            async Task RemoveRealCache()
            {
                hideErrors = ResolveHideErrors(hideErrors);

                try
                {
                    await cacheSupportsMultipleItems.RemoveManyAsync(keyArray.Select(NormalizeKey), token);
                }
                catch (Exception ex)
                {
                    if (hideErrors == true)
                    {
                        await HandleExceptionAsync(ex);
                        return;
                    }

                    throw;
                }
            }

            if (ShouldConsiderUow(considerUow))
            {
                var uowCache = GetUnitOfWorkCache();

                foreach (var key in keyArray)
                {
                    if (uowCache.TryGetValue(key, out _))
                    {
                        uowCache[key].RemoveValue();
                    }
                }

                UnitOfWorkManager.Current?.OnCompleted(RemoveRealCache);
            }
            else
            {
                await RemoveRealCache();
            }
        }
        else
        {
            foreach (var key in keyArray)
            {
                await RemoveAsync(key, hideErrors, considerUow, token);
            }
        }
    }

    protected virtual void HandleException(Exception ex)
    {
        _ = HandleExceptionAsync(ex);
    }

    protected virtual async Task HandleExceptionAsync(Exception ex)
    {
        Logger.LogWarning(ex, "Cache exception occurred.");

        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var exceptionNotifier = scope.ServiceProvider.GetService<IExceptionNotifier>();
            if (exceptionNotifier != null)
            {
                await exceptionNotifier.NotifyAsync(new ExceptionNotificationContext(ex, LogLevel.Warning));
            }
        }
    }

    protected virtual KeyValuePair<TCacheKey, TCacheItem?>[] ToCacheItems(byte[]?[] itemBytes, TCacheKey[] itemKeys)
    {
        if (itemBytes.Length != itemKeys.Length)
        {
            throw new InvalidOperationException(
                $"Count of item payloads ({itemBytes.Length}) must match key count ({itemKeys.Length}).");
        }

        var result = new List<KeyValuePair<TCacheKey, TCacheItem?>>();

        for (var i = 0; i < itemKeys.Length; i++)
        {
            result.Add(new KeyValuePair<TCacheKey, TCacheItem?>(itemKeys[i], ToCacheItem(itemBytes[i])));
        }

        return result.ToArray();
    }

    protected virtual TCacheItem? ToCacheItem(byte[]? bytes)
    {
        if (bytes == null)
        {
            return null;
        }

        return Serializer.Deserialize<TCacheItem>(bytes);
    }

    protected virtual KeyValuePair<string, byte[]>[] ToRawCacheItems(KeyValuePair<TCacheKey, TCacheItem>[] items)
    {
        return items.Select(i => new KeyValuePair<string, byte[]>(NormalizeKey(i.Key), Serializer.Serialize(i.Value))).ToArray();
    }

    private static KeyValuePair<TCacheKey, TCacheItem?>[] ToCacheItemsWithDefaultValues(TCacheKey[] keys)
    {
        return keys.Select(key => new KeyValuePair<TCacheKey, TCacheItem?>(key, default)).ToArray();
    }

    protected virtual bool ShouldConsiderUow(bool considerUow)
    {
        return considerUow && UnitOfWorkManager.Current != null;
    }

    protected virtual string GetUnitOfWorkCacheKey()
    {
        return UowCacheName + CacheName;
    }

    protected virtual Dictionary<TCacheKey, UnitOfWorkCacheItem<TCacheItem>> GetUnitOfWorkCache()
    {
        if (UnitOfWorkManager.Current == null)
        {
            throw new InvalidOperationException("There is no active UOW.");
        }

        return UnitOfWorkManager.Current.GetOrAddItem(GetUnitOfWorkCacheKey(), key => new Dictionary<TCacheKey, UnitOfWorkCacheItem<TCacheItem>>());
    }
}

public class UnitOfWorkCacheItem<TValue>
{
    public TValue? Value { get; private set; }
    public bool IsRemoved { get; private set; }

    public UnitOfWorkCacheItem(TValue? value)
    {
        Value = value;
    }

    public void SetValue(TValue? value)
    {
        Value = value;
        IsRemoved = false;
    }

    public void RemoveValue()
    {
        Value = default;
        IsRemoved = true;
    }

    public TValue? GetUnRemovedValueOrNull()
    {
        return IsRemoved ? default : Value;
    }
}
