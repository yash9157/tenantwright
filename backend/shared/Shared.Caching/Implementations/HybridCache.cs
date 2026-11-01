using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Caching.Abstractions;
using Shared.Threading;
using Shared.Uow.Abstractions;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Shared.Caching.Implementations;

public class SharedHybridCache<TCacheItem> : IHybridCache<TCacheItem>
    where TCacheItem : class
{
    public IHybridCache<TCacheItem, string> InternalCache { get; }

    public SharedHybridCache(IHybridCache<TCacheItem, string> internalCache)
    {
        InternalCache = internalCache;
    }

    public virtual async Task<TCacheItem?> GetOrCreateAsync(string key, Func<Task<TCacheItem>> factory, Func<HybridCacheEntryOptions>? optionsFactory = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        return await InternalCache.GetOrCreateAsync(key, factory, optionsFactory, hideErrors, considerUow, token);
    }

    public virtual async Task SetAsync(string key, TCacheItem value, HybridCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        await InternalCache.SetAsync(key, value, options, hideErrors, considerUow, token);
    }

    public virtual async Task RemoveAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        await InternalCache.RemoveAsync(key, hideErrors, considerUow, token);
    }

    public virtual async Task RemoveManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
    {
        await InternalCache.RemoveManyAsync(keys, hideErrors, considerUow, token);
    }
}

public class SharedHybridCacheSerializerFactory : IHybridCacheSerializerFactory
{
    protected JsonSerializerOptions Options { get; }

    public SharedHybridCacheSerializerFactory(IOptions<JsonSerializerOptions> options)
    {
        Options = options.Value;
    }

    public bool TryCreateSerializer<T>([NotNullWhen(true)] out IHybridCacheSerializer<T>? serializer)
    {
        if (typeof(T) == typeof(string) || typeof(T) == typeof(byte[]))
        {
            serializer = null;
            return false;
        }

        serializer = new SharedHybridCacheJsonSerializer<T>(Options);
        return true;
    }
}

public class SharedHybridCacheJsonSerializer<T> : IHybridCacheSerializer<T>
{
    private readonly JsonSerializerOptions _options;

    public SharedHybridCacheJsonSerializer(JsonSerializerOptions options)
    {
        _options = options;
    }

    public T Deserialize(ReadOnlySequence<byte> source)
    {
        if (source.IsSingleSegment)
        {
            return JsonSerializer.Deserialize<T>(source.FirstSpan, _options)!;
        }
        return JsonSerializer.Deserialize<T>(source.ToArray(), _options)!;
    }

    public void Serialize(T value, IBufferWriter<byte> target)
    {
        using var writer = new Utf8JsonWriter(target);
        JsonSerializer.Serialize(writer, value, _options);
    }
}

public class SharedHybridCache<TCacheItem, TCacheKey> : IHybridCache<TCacheItem, TCacheKey>
    where TCacheItem : class
    where TCacheKey : notnull
{
    public const string UowCacheName = "SharedHybridCache";

    public ILogger<SharedHybridCache<TCacheItem, TCacheKey>> Logger { get; set; }

    protected string CacheName { get; set; } = default!;

    protected bool IgnoreMultiTenancy { get; set; }

    protected IServiceProvider ServiceProvider { get; }

    protected HybridCache HybridCache { get; }

    protected Microsoft.Extensions.Caching.Distributed.IDistributedCache DistributedCache { get; }

    protected ICancellationTokenProvider CancellationTokenProvider { get; }

    protected IDistributedCacheKeyNormalizer KeyNormalizer { get; }

    protected IServiceScopeFactory ServiceScopeFactory { get; }

    protected IUnitOfWorkManager UnitOfWorkManager { get; }

    protected SemaphoreSlim SyncSemaphore { get; }

    protected HybridCacheEntryOptions DefaultCacheOptions { get; set; } = default!;

    protected SharedHybridCacheOptions HybridCacheOption { get; }

    public SharedHybridCache(
        IServiceProvider serviceProvider,
        IOptions<SharedHybridCacheOptions> hybridCacheOption,
        HybridCache hybridCache,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache distributedCache,
        ICancellationTokenProvider cancellationTokenProvider,
        IDistributedCacheKeyNormalizer keyNormalizer,
        IServiceScopeFactory serviceScopeFactory,
        IUnitOfWorkManager unitOfWorkManager)
    {
        ServiceProvider = serviceProvider;
        HybridCacheOption = hybridCacheOption.Value;
        HybridCache = hybridCache;
        DistributedCache = distributedCache;
        CancellationTokenProvider = cancellationTokenProvider;
        Logger = NullLogger<SharedHybridCache<TCacheItem, TCacheKey>>.Instance;
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

    protected virtual HybridCacheEntryOptions GetDefaultCacheEntryOptions()
    {
        foreach (var configure in HybridCacheOption.CacheConfigurators)
        {
            var options = configure.Invoke(CacheName);
            if (options != null)
            {
                return options;
            }
        }

        return HybridCacheOption.GlobalHybridCacheEntryOptions;
    }

    protected virtual bool ResolveHideErrors(bool? hideErrors)
    {
        if (hideErrors.HasValue)
        {
            return hideErrors.Value;
        }

        foreach (var configure in HybridCacheOption.HideErrorConfigurators)
        {
            var configuredHideErrors = configure.Invoke(CacheName);
            if (configuredHideErrors.HasValue)
            {
                return configuredHideErrors.Value;
            }
        }

        return HybridCacheOption.HideErrors;
    }

    protected virtual void SetDefaultOptions()
    {
        CacheName = CacheNameAttribute.GetCacheName(typeof(TCacheItem));
        IgnoreMultiTenancy = typeof(TCacheItem).IsDefined(typeof(IgnoreCacheMultiTenancyAttribute), true);

        //Configure default cache entry options
        DefaultCacheOptions = GetDefaultCacheEntryOptions();
    }

    public virtual async Task<TCacheItem?> GetOrCreateAsync(
        TCacheKey key,
        Func<Task<TCacheItem>> factory,
        Func<HybridCacheEntryOptions>? optionsFactory = null,
        bool? hideErrors = null,
        bool considerUow = false,
        CancellationToken token = default)
    {
        token = CancellationTokenProvider.FallbackToProvider(token);
        hideErrors = ResolveHideErrors(hideErrors);

        TCacheItem? value = null;

        if (!considerUow)
        {
            try
            {
                value = await HybridCache.GetOrCreateAsync(
                    key: NormalizeKey(key),
                    factory: async cancel => await factory(),
                    options: optionsFactory?.Invoke(),
                    tags: null,
                    cancellationToken: token);
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

            return value;
        }

        try
        {
            using (await SyncSemaphore.LockAsync(token))
            {
                if (ShouldConsiderUow(considerUow))
                {
                    value = GetUnitOfWorkCache().GetValueOrDefault(key)?.GetUnRemovedValueOrNull();
                    if (value != null)
                    {
                        return value;
                    }
                }

                var existingValue = await TryGetExistingHybridValueAsync(key, optionsFactory, token);
                if (existingValue.Found)
                {
                    return existingValue.Value;
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

        return value;
    }

    public virtual async Task SetAsync(
        TCacheKey key,
        TCacheItem value,
        HybridCacheEntryOptions? options = null,
        bool? hideErrors = null,
        bool considerUow = false,
        CancellationToken token = default)
    {
        async Task SetRealCache()
        {
            token = CancellationTokenProvider.FallbackToProvider(token);
            hideErrors = ResolveHideErrors(hideErrors);

            try
            {
                await HybridCache.SetAsync(
                    key: NormalizeKey(key),
                    value: value,
                    options: options ?? GetDefaultCacheEntryOptions(),
                    tags: null,
                    cancellationToken: token
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

    public virtual async Task RemoveAsync(
        TCacheKey key,
        bool? hideErrors = null,
        bool considerUow = false,
        CancellationToken token = default)
    {
        await RemoveManyAsync(new[] { key }, hideErrors, considerUow, token);
    }

    public async Task RemoveManyAsync(
        IEnumerable<TCacheKey> keys,
        bool? hideErrors = null,
        bool considerUow = false,
        CancellationToken token = default)
    {
        var keyArray = keys.ToArray();

        async Task RemoveRealCache()
        {
            hideErrors = ResolveHideErrors(hideErrors);

            try
            {
                await HybridCache.RemoveAsync(
                    keyArray.Select(NormalizeKey), token);
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

    protected virtual async Task HandleExceptionAsync(Exception ex)
    {
        Logger.LogWarning(ex, "Hybrid cache exception occurred.");

        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var exceptionNotifier = scope.ServiceProvider.GetService<IExceptionNotifier>();
            if (exceptionNotifier != null)
            {
                await exceptionNotifier.NotifyAsync(new ExceptionNotificationContext(ex, LogLevel.Warning));
            }
        }
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

        return UnitOfWorkManager.Current.GetOrAddItem(GetUnitOfWorkCacheKey(),
            key => new Dictionary<TCacheKey, UnitOfWorkCacheItem<TCacheItem>>());
    }

    private async Task<(bool Found, TCacheItem? Value)> TryGetExistingHybridValueAsync(
        TCacheKey key,
        Func<HybridCacheEntryOptions>? optionsFactory,
        CancellationToken token)
    {
        try
        {
            var value = await HybridCache.GetOrCreateAsync(
                key: NormalizeKey(key),
                factory: static _ => ValueTask.FromException<TCacheItem>(new HybridCacheReadMissException()),
                options: optionsFactory?.Invoke(),
                tags: null,
                cancellationToken: token);

            return (true, value);
        }
        catch (HybridCacheReadMissException)
        {
            return (false, null);
        }
    }

    private sealed class HybridCacheReadMissException : Exception
    {
        public HybridCacheReadMissException()
            : base("Hybrid cache read missed.")
        {
        }
    }
}
