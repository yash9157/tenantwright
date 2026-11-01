using Microsoft.Extensions.Caching.Distributed;
using Notifications.Application.Contracts.RateLimiting;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.RateLimiting;

/// <summary>
/// Fixed-window counter over the shared distributed cache (Valkey). The window is bucketed by
/// <c>floor(now / window)</c> so each window has its own key that expires automatically.
/// <para>
/// NOTE: the shared <see cref="IDistributedCache"/> abstraction exposes only get/set, so the
/// increment is read-modify-write and NOT atomic. Concurrent callers/replicas race on the same
/// bucket and lose updates (two readers see the same value and both write value+amount), so under
/// real concurrency the counter systematically UNDER-counts and the effective limit is looser than
/// configured — it does not merely "slip past the boundary". This is acceptable only as a coarse,
/// disabled-by-default guard against a runaway publisher. For an accurate limit, replace this with
/// an atomic Valkey primitive (INCR/EXPIRE, or a sorted-set sliding window).
/// </para>
/// </summary>
public sealed class DistributedCacheNotificationRateLimitStore : INotificationRateLimitStore, ITransientDependency
{
    private readonly IDistributedCache _cache;

    public DistributedCacheNotificationRateLimitStore(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task<long> IncrementAndGetAsync(
        string key,
        TimeSpan window,
        int amount,
        CancellationToken cancellationToken = default)
    {
        var windowSeconds = Math.Max(1, (long)window.TotalSeconds);
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bucket = nowSeconds / windowSeconds;
        var bucketKey = $"{key}:{bucket}";

        var existing = await _cache.GetAsync(bucketKey, cancellationToken);
        var current = existing is { Length: 8 } ? BitConverter.ToInt64(existing) : 0L;
        current += amount;

        await _cache.SetAsync(
            bucketKey,
            BitConverter.GetBytes(current),
            new DistributedCacheEntryOptions
            {
                // Keep the bucket alive past the window edge so late reads in the same window still see it.
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(windowSeconds * 2)
            },
            cancellationToken);

        return current;
    }
}
