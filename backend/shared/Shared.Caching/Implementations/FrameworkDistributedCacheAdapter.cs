using Microsoft.Extensions.Caching.Distributed;
using Shared.Modularity.DependencyInjection;

namespace Shared.Caching.Implementations;

public sealed class FrameworkDistributedCacheAdapter : DistributedCacheBase, ISingletonDependency
{
    private readonly Microsoft.Extensions.Caching.Distributed.IDistributedCache _cache;

    public FrameworkDistributedCacheAdapter(
        Microsoft.Extensions.Caching.Distributed.IDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public override byte[]? Get(string key) => _cache.Get(key);

    public override Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _cache.GetAsync(key, cancellationToken);
    }

    public override void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        _cache.Set(key, value, options);

    public override Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _cache.SetAsync(key, value, options, cancellationToken);
    }

    public override void Refresh(string key) => _cache.Refresh(key);

    public override Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _cache.RefreshAsync(key, cancellationToken);
    }

    public override void Remove(string key) => _cache.Remove(key);

    public override Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _cache.RemoveAsync(key, cancellationToken);
    }
}
