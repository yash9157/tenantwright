using Microsoft.Extensions.Caching.Hybrid;
using Shared.EventContracts;
using Shared.EventBus.Messaging;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Caching;

public class PermissionStampService : ITransientDependency
{
    private static readonly HybridCacheEntryOptions StampCacheOptions = new()
    {
        Expiration = TimeSpan.FromHours(1)
    };

    private readonly PermissionProviderStampCache _stampCache;
    private readonly ITenantWrightMessageSender _messageSender;

    public PermissionStampService(PermissionProviderStampCache stampCache, ITenantWrightMessageSender messageSender)
    {
        _stampCache = stampCache;
        _messageSender = messageSender;
    }

    public async Task<long> GetAsync(Guid? tenantId, string providerName, string providerKey, CancellationToken cancellationToken = default)
    {
        var key = PermissionCacheKeys.BuildProviderKey(tenantId, providerName, providerKey);
        var item = await _stampCache.GetOrCreateAsync(
            key,
            () => Task.FromResult(new PermissionProviderStampCacheItem { Stamp = 0 }),
            optionsFactory: () => StampCacheOptions,
            considerUow: false,
            token: cancellationToken);

        var stamp = item?.Stamp ?? 0;
        if (stamp <= 0)
        {
            await _stampCache.RemoveAsync(key, considerUow: false, token: cancellationToken);
        }

        return stamp;
    }

    public async Task SetAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        long stamp,
        bool considerUow = true,
        CancellationToken cancellationToken = default)
    {
        if (stamp <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stamp), "Permission provider stamp must be greater than zero.");
        }

        var key = PermissionCacheKeys.BuildProviderKey(tenantId, providerName, providerKey);
        await _stampCache.SetAsync(
            key,
            new PermissionProviderStampCacheItem { Stamp = stamp },
            options: StampCacheOptions,
            considerUow: considerUow,
            token: cancellationToken);
    }

    public async Task<long> BumpAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        long? stamp = null,
        bool considerUow = true,
        CancellationToken cancellationToken = default)
    {
        stamp ??= await GetNextStampAsync(tenantId, providerName, providerKey, cancellationToken);
        await SetAsync(tenantId, providerName, providerKey, stamp.Value, considerUow, cancellationToken);

        if (tenantId.HasValue && tenantId.Value != Guid.Empty)
        {
            await _messageSender.PublishEventAsync(
                new PermissionGrantsChangedTenantEto
                {
                    TenantId = tenantId.Value,
                    ProviderName = providerName,
                    ProviderKey = providerKey,
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Stamp = stamp.Value
                }, cancellationToken);
        }
        else
        {
            await _messageSender.PublishEventAsync(
                new PermissionGrantsChangedHostEto
                {
                    ProviderName = providerName,
                    ProviderKey = providerKey,
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Stamp = stamp.Value
                }, cancellationToken);
        }

        return stamp.Value;
    }

    /// <summary>
    /// Computes a fresh, strictly-increasing stamp for a provider by reading its current stamp and
    /// advancing past it. Callers that need to synchronize a grants cache entry and the stamp with the
    /// same value (sync-then-bump) should call this once and reuse the result for both, rather than each
    /// independently reading <see cref="DateTime.UtcNow"/>, which can collide within the same tick
    /// resolution window and leave a stale snapshot cached under an unchanged key.
    /// </summary>
    public async Task<long> GetNextStampAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(tenantId, providerName, providerKey, cancellationToken);
        return ComputeNextStamp(current);
    }

    /// <summary>
    /// Advances past <paramref name="currentStamp"/> to the next strictly-increasing value: the current
    /// stamp plus one, or <see cref="DateTime.UtcNow"/> ticks, whichever is larger. This guarantees the
    /// returned stamp is always greater than <paramref name="currentStamp"/> even if two bumps for the
    /// same provider happen within the same UTC tick. Concurrent bumps to the same provider from
    /// different pods can still race on a single tick (no atomic increment is available via
    /// <c>IDistributedCache</c>); the event-driven cache invalidation backstops that residual case.
    /// </summary>
    public static long ComputeNextStamp(long currentStamp)
    {
        var utcNowTicks = DateTime.UtcNow.Ticks;
        return currentStamp >= utcNowTicks ? currentStamp + 1 : utcNowTicks;
    }
}
