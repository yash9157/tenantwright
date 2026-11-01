using Microsoft.Extensions.Caching.Distributed;
using Shared.Caching.Abstractions;

namespace Shared.AspNetCore.Host.Idempotency;

public sealed class DistributedCacheIdempotencyStore : IIdempotencyStore
{
    private readonly IDistributedCache<IdempotencyCacheEntry, string> _cache;

    public DistributedCacheIdempotencyStore(IDistributedCache<IdempotencyCacheEntry, string> cache)
    {
        _cache = cache;
    }

    public Task<IdempotencyCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        return _cache.GetAsync(key, token: cancellationToken);
    }

    public Task SetInProgressAsync(string key, string requestFingerprint, TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var entry = new IdempotencyCacheEntry
        {
            State = IdempotencyRecordState.InProgress,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RequestFingerprint = requestFingerprint
        };

        return _cache.SetAsync(
            key,
            entry,
            options: new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = NormalizeTtl(ttl)
            },
            token: cancellationToken);
    }

    public Task SetCompletedAsync(
        string key,
        string requestFingerprint,
        int statusCode,
        string? contentType,
        string? responseBody,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var entry = new IdempotencyCacheEntry
        {
            State = IdempotencyRecordState.Completed,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RequestFingerprint = requestFingerprint,
            StatusCode = statusCode,
            ContentType = contentType,
            ResponseBody = responseBody
        };

        return _cache.SetAsync(
            key,
            entry,
            options: new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = NormalizeTtl(ttl)
            },
            token: cancellationToken);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        return _cache.RemoveAsync(key, token: cancellationToken);
    }

    private static TimeSpan NormalizeTtl(TimeSpan ttl)
    {
        return ttl <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : ttl;
    }
}
