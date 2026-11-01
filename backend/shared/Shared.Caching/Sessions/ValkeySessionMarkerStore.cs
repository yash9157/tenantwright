using System.Text;
using Shared.Caching.Abstractions;
using Shared.Modularity.DependencyInjection;

namespace Shared.Caching.Sessions;

public sealed class ValkeySessionMarkerStore : ISessionMarkerStore, ITransientDependency
{
    private readonly Shared.Caching.Abstractions.IDistributedCache _distributedCache;

    public ValkeySessionMarkerStore(Shared.Caching.Abstractions.IDistributedCache distributedCache)
    {
        _distributedCache = distributedCache;
    }

    public async Task SetActiveAsync(
        Guid? tenantId,
        Guid userId,
        string sessionId,
        string value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var key = SessionMarkerKey.Build(tenantId, userId, sessionId);
        var bytes = Encoding.UTF8.GetBytes(value);
        await _distributedCache.SetAsync(
            key,
            bytes,
            new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl
            },
            cancellationToken);
    }

    public async Task<string?> IsActiveAsync(
        Guid? tenantId,
        Guid userId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var key = SessionMarkerKey.Build(tenantId, userId, sessionId);
        var payload = await _distributedCache.GetAsync(key, cancellationToken);
        if (payload == null || payload.Length == 0)
        {
            return null;
        }

        return Encoding.UTF8.GetString(payload);
    }

    public Task RemoveAsync(
        Guid? tenantId,
        Guid userId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.CompletedTask;
        }

        var key = SessionMarkerKey.Build(tenantId, userId, sessionId);
        return _distributedCache.RemoveAsync(key, cancellationToken);
    }
}
