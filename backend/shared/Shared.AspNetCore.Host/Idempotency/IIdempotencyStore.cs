namespace Shared.AspNetCore.Host.Idempotency;

public interface IIdempotencyStore
{
    Task<IdempotencyCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetInProgressAsync(string key, string requestFingerprint, TimeSpan ttl,
        CancellationToken cancellationToken = default);

    Task SetCompletedAsync(
        string key,
        string requestFingerprint,
        int statusCode,
        string? contentType,
        string? responseBody,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
