using System.Reflection;

namespace Shared.Caching;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct, Inherited = true, AllowMultiple = false)]
public sealed class CacheNameAttribute : Attribute
{
    public string Name { get; }

    public CacheNameAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Cache name cannot be null or whitespace.", nameof(name));
        }

        Name = name.Trim();
    }

    public static string GetCacheName(Type cacheItemType)
    {
        ArgumentNullException.ThrowIfNull(cacheItemType);

        var cacheName = cacheItemType.GetCustomAttribute<CacheNameAttribute>(inherit: true)?.Name;
        if (!string.IsNullOrWhiteSpace(cacheName))
        {
            return cacheName;
        }

        var fallback = cacheItemType.FullName ?? cacheItemType.Name;
        const string postfix = "CacheItem";
        if (fallback.EndsWith(postfix, StringComparison.Ordinal))
        {
            fallback = fallback[..^postfix.Length];
        }

        return fallback;
    }
}
