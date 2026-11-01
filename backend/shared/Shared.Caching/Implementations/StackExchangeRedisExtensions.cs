using StackExchange.Redis;
using System.Text;

namespace Shared.Caching.Implementations;

internal static class StackExchangeRedisExtensions
{
    public static RedisValue[][] HashMemberGetMany(this IDatabase cache, RedisKey[] keys, RedisValue[] fields)
    {
        var tasks = new Task<RedisValue[]>[keys.Length];
        var results = new RedisValue[keys.Length][];

        for (var i = 0; i < keys.Length; i++)
        {
            tasks[i] = cache.HashGetAsync(keys[i], fields);
        }

        Task.WaitAll(tasks);

        for (var i = 0; i < tasks.Length; i++)
        {
            results[i] = tasks[i].GetAwaiter().GetResult();
        }

        return results;
    }

    public static Task<RedisValue[][]> HashMemberGetManyAsync(this IDatabase cache, RedisKey[] keys, RedisValue[] fields)
    {
        var tasks = new Task<RedisValue[]>[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            tasks[i] = cache.HashGetAsync(keys[i], fields);
        }

        return Task.WhenAll(tasks);
    }

    public static RedisKey AppendKey(this RedisKey prefix, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return prefix;
        }

        byte[]? prefixBytes = prefix;
        if (prefixBytes == null || prefixBytes.Length == 0)
        {
            return (RedisKey)key;
        }

        var keyBytes = Encoding.UTF8.GetBytes(key);
        var combined = new byte[prefixBytes.Length + keyBytes.Length];
        Buffer.BlockCopy(prefixBytes, 0, combined, 0, prefixBytes.Length);
        Buffer.BlockCopy(keyBytes, 0, combined, prefixBytes.Length, keyBytes.Length);
        return (RedisKey)combined;
    }
}
