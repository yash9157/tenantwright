using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using Shared.Caching.Abstractions;
using Shared.Modularity.DependencyInjection;
using StackExchange.Redis;

namespace Shared.Caching.Implementations;

public sealed class ValkeyCache :
    DistributedCacheBase,
    Microsoft.Extensions.Caching.Distributed.IDistributedCache,
    ICacheSupportsMultipleItems,
    ISingletonDependency,
    IDisposable
{
    private const string AbsoluteExpirationKey = "absexp";
    private const string SlidingExpirationKey = "sldexp";
    private const string DataKey = "data";
    private const long NotPresent = -1;

    private static readonly RedisValue[] DataFields =
        [AbsoluteExpirationKey, SlidingExpirationKey, DataKey];
    private static readonly RedisValue[] MetadataFields =
        [AbsoluteExpirationKey, SlidingExpirationKey];

    private readonly Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache _inner;
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly RedisKey _instancePrefix;

    public ValkeyCache(
        IOptions<RedisCacheOptions> options,
        IConnectionMultiplexer connectionMultiplexer)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionMultiplexer = connectionMultiplexer
            ?? throw new ArgumentNullException(nameof(connectionMultiplexer));
        _inner = new Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache(options);
        _instancePrefix = string.IsNullOrEmpty(options.Value.InstanceName)
            ? (RedisKey)string.Empty
            : (RedisKey)Encoding.UTF8.GetBytes(options.Value.InstanceName);
    }

    public override byte[]? Get(string key) => _inner.Get(key);

    public override Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        _inner.GetAsync(key, cancellationToken);

    public override void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        _inner.Set(key, value, options);

    public override Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken cancellationToken = default) =>
        _inner.SetAsync(key, value, options, cancellationToken);

    public override void Refresh(string key) => _inner.Refresh(key);

    public override Task RefreshAsync(string key, CancellationToken cancellationToken = default) =>
        _inner.RefreshAsync(key, cancellationToken);

    public override void Remove(string key) => _inner.Remove(key);

    public override Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        _inner.RemoveAsync(key, cancellationToken);

    public override byte[]?[] GetMany(IEnumerable<string> keys) =>
        GetManyAsync(keys).GetAwaiter().GetResult();

    public override async Task<byte[]?[]> GetManyAsync(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        var database = _connectionMultiplexer.GetDatabase();
        var valkeyKeys = keys.Select(GetKey).ToArray();
        var results = await database.HashMemberGetManyAsync(valkeyKeys, DataFields).ConfigureAwait(false);
        var values = new byte[]?[results.Length];
        var refreshTasks = new List<Task>();

        for (var index = 0; index < results.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = results[index];
            if (fields.Length >= 3 && fields[2].HasValue)
            {
                values[index] = (byte[]?)fields[2];
            }

            var expiration = GetRefreshExpiration(fields);
            if (expiration.HasValue)
            {
                refreshTasks.Add(database.KeyExpireAsync(valkeyKeys[index], expiration));
            }
        }

        await Task.WhenAll(refreshTasks).ConfigureAwait(false);
        return values;
    }

    public override void SetMany(
        IEnumerable<KeyValuePair<string, byte[]>> items,
        DistributedCacheEntryOptions options) =>
        SetManyAsync(items, options).GetAwaiter().GetResult();

    public override async Task SetManyAsync(
        IEnumerable<KeyValuePair<string, byte[]>> items,
        DistributedCacheEntryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var database = _connectionMultiplexer.GetDatabase();
        var now = DateTimeOffset.UtcNow;
        var absoluteExpiration = GetAbsoluteExpiration(now, options);
        var expiration = GetExpiration(now, absoluteExpiration, options);
        var tasks = new List<Task>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = GetKey(item.Key);
            tasks.Add(database.HashSetAsync(
                key,
                [
                    new HashEntry(AbsoluteExpirationKey, absoluteExpiration?.Ticks ?? NotPresent),
                    new HashEntry(SlidingExpirationKey, options.SlidingExpiration?.Ticks ?? NotPresent),
                    new HashEntry(DataKey, item.Value)
                ]));
            if (expiration.HasValue)
            {
                tasks.Add(database.KeyExpireAsync(key, expiration));
            }
            else
            {
                tasks.Add(database.KeyPersistAsync(key));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public override void RefreshMany(IEnumerable<string> keys) =>
        RefreshManyAsync(keys).GetAwaiter().GetResult();

    public override async Task RefreshManyAsync(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        var database = _connectionMultiplexer.GetDatabase();
        var valkeyKeys = keys.Select(GetKey).ToArray();
        var results = await database.HashMemberGetManyAsync(valkeyKeys, MetadataFields).ConfigureAwait(false);
        var tasks = new List<Task>();
        for (var index = 0; index < results.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expiration = GetRefreshExpiration(results[index]);
            if (expiration.HasValue)
            {
                tasks.Add(database.KeyExpireAsync(valkeyKeys[index], expiration));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public override void RemoveMany(IEnumerable<string> keys) =>
        RemoveManyAsync(keys).GetAwaiter().GetResult();

    public override async Task RemoveManyAsync(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        var valkeyKeys = keys.Select(GetKey).ToArray();
        if (valkeyKeys.Length > 0)
        {
            await _connectionMultiplexer.GetDatabase().KeyDeleteAsync(valkeyKeys).ConfigureAwait(false);
        }
    }

    public void Dispose() => _inner.Dispose();

    private RedisKey GetKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _instancePrefix.AppendKey(key);
    }

    private static DateTimeOffset? GetAbsoluteExpiration(
        DateTimeOffset now,
        DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpirationRelativeToNow.HasValue)
        {
            return now.Add(options.AbsoluteExpirationRelativeToNow.Value);
        }

        if (options.AbsoluteExpiration.HasValue && options.AbsoluteExpiration.Value <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.AbsoluteExpiration,
                "The absolute expiration value must be in the future.");
        }

        return options.AbsoluteExpiration;
    }

    private static TimeSpan? GetExpiration(
        DateTimeOffset now,
        DateTimeOffset? absoluteExpiration,
        DistributedCacheEntryOptions options)
    {
        if (absoluteExpiration.HasValue && options.SlidingExpiration.HasValue)
        {
            return Min(absoluteExpiration.Value - now, options.SlidingExpiration.Value);
        }

        return absoluteExpiration.HasValue
            ? absoluteExpiration.Value - now
            : options.SlidingExpiration;
    }

    private static TimeSpan? GetRefreshExpiration(RedisValue[] fields)
    {
        if (fields.Length < 2 ||
            !long.TryParse(fields[0].ToString(), out var absoluteTicks) ||
            !long.TryParse(fields[1].ToString(), out var slidingTicks) ||
            slidingTicks == NotPresent)
        {
            return null;
        }

        var sliding = TimeSpan.FromTicks(slidingTicks);
        if (absoluteTicks == NotPresent)
        {
            return sliding;
        }

        var remaining = new DateTimeOffset(absoluteTicks, TimeSpan.Zero) - DateTimeOffset.UtcNow;
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : Min(remaining, sliding);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
