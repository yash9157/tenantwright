using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Permissions.Application.Caching;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Evaluation;

public class PermissionSnapshotBuilder : ITransientDependency
{
    private static readonly TimeSpan MaxSnapshotCacheExpiration = TimeSpan.FromMinutes(20);

    private readonly IRepository<PermissionGrant, Guid> _grantRepository;
    private readonly PermissionProviderCacheRepairService _cacheRepairService;
    private readonly PermissionSnapshotCache _snapshotCache;
    private readonly ILogger<PermissionSnapshotBuilder> _logger;

    public PermissionSnapshotBuilder(
        IRepository<PermissionGrant, Guid> grantRepository,
        PermissionProviderCacheRepairService cacheRepairService,
        PermissionSnapshotCache snapshotCache,
        ILogger<PermissionSnapshotBuilder> logger)
    {
        _grantRepository = grantRepository;
        _cacheRepairService = cacheRepairService;
        _snapshotCache = snapshotCache;
        _logger = logger;
    }

    public async Task<(PermissionSnapshotCacheItem Snapshot, string SnapshotHash)> GetOrBuildAsync(
        Guid? tenantId,
        Guid userId,
        IReadOnlyList<Guid> roleIds,
        CancellationToken cancellationToken = default)
    {
        var userStamp = await _cacheRepairService.EnsureProviderCacheAsync(tenantId, PermissionProviderNames.User, userId.ToString("D"), cancellationToken);
        var normalizedRoleIds = roleIds.Where(x => x != Guid.Empty).Distinct().OrderBy(x => x).ToArray();
        var roleStamps = new List<long>(normalizedRoleIds.Length);
        foreach (var roleId in normalizedRoleIds)
        {
            roleStamps.Add(await _cacheRepairService.EnsureProviderCacheAsync(
                tenantId,
                PermissionProviderNames.Role,
                roleId.ToString("D"),
                cancellationToken));
        }

        var combinedStamp = CombineStamp(userStamp, roleStamps);
        var rolesHash = PermissionSnapshotKeyHelper.ComputeRoleIdentityHash(normalizedRoleIds);
        var snapshotHash = PermissionSnapshotKeyHelper.ComputeSnapshotHash(userId, rolesHash, combinedStamp);
        var cacheKey = PermissionSnapshotKeyHelper.BuildCacheKey(userId, rolesHash, combinedStamp);

        if (combinedStamp <= 0)
        {
            var (uncachedSnapshot, _) = await BuildAsync(tenantId, userId, normalizedRoleIds, combinedStamp, cancellationToken);
            return (uncachedSnapshot, snapshotHash);
        }

        // Captured by the optionsFactory closure once BuildAsync (the cache-miss factory) has run, so the
        // write path below caps the entry's TTL at the nearest grant expiry instead of the flat 20-minute
        // default. A cached snapshot cannot re-check expiry on its own, so this bounds how stale an
        // expired-but-still-cached grant can remain visible; the sweep worker additionally bumps the
        // stamp on expiry so most callers invalidate well before this cap is reached.
        TimeSpan? cappedExpiration = null;
        HybridCacheEntryOptions OptionsFactory() => new() { Expiration = cappedExpiration ?? MaxSnapshotCacheExpiration };

        PermissionSnapshotCacheItem? snapshot;
        try
        {
            snapshot = await _snapshotCache.GetOrCreateAsync(
                cacheKey,
                async () =>
                {
                    var (built, nearestExpiryUtc) = await BuildAsync(tenantId, userId, normalizedRoleIds, combinedStamp, cancellationToken);
                    cappedExpiration = ComputeCacheExpiration(nearestExpiryUtc);
                    return built;
                },
                optionsFactory: OptionsFactory,
                considerUow: true,
                token: cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Corrupted permission snapshot cache entry detected for key {CacheKey}. Rebuilding snapshot.", cacheKey);
            await _snapshotCache.RemoveAsync(cacheKey, considerUow: true, token: cancellationToken);
            snapshot = null;
        }

        if (snapshot == null)
        {
            var (built, nearestExpiryUtc) = await BuildAsync(tenantId, userId, normalizedRoleIds, combinedStamp, cancellationToken);
            snapshot = built;
            await _snapshotCache.SetAsync(
                cacheKey,
                snapshot,
                options: new HybridCacheEntryOptions { Expiration = ComputeCacheExpiration(nearestExpiryUtc) },
                considerUow: true,
                token: cancellationToken);
        }

        return (snapshot, snapshotHash);
    }

    /// <summary>
    /// Caps the snapshot cache TTL at the nearest upcoming grant expiry so a time-bound grant cannot
    /// remain cached as granted well past its expiry: <c>min(MaxSnapshotCacheExpiration, nearestExpiryUtc - now)</c>.
    /// Falls back to <see cref="MaxSnapshotCacheExpiration"/> when there is no expiring grant.
    /// </summary>
    public static TimeSpan ComputeCacheExpiration(DateTime? nearestExpiryUtc)
    {
        if (!nearestExpiryUtc.HasValue)
        {
            return MaxSnapshotCacheExpiration;
        }

        var untilExpiry = nearestExpiryUtc.Value - DateTime.UtcNow;
        if (untilExpiry <= TimeSpan.Zero)
        {
            // Defensive floor: the eval query excludes already-expired grants, so this should not
            // normally be reachable, but avoid caching with a zero/negative TTL if it ever is.
            return TimeSpan.FromSeconds(1);
        }

        return untilExpiry < MaxSnapshotCacheExpiration ? untilExpiry : MaxSnapshotCacheExpiration;
    }

    private async Task<(PermissionSnapshotCacheItem Snapshot, DateTime? NearestExpiryUtc)> BuildAsync(
        Guid? tenantId,
        Guid userId,
        IReadOnlyList<Guid> roleIds,
        long combinedStamp,
        CancellationToken cancellationToken)
    {
        var userKey = userId.ToString("D");
        var roleKeys = roleIds.Select(x => x.ToString("D")).ToList();

        var utcNow = DateTime.UtcNow;
        var queryable = await _grantRepository.GetQueryableAsync();
        var grants = await queryable
            .Where(x => x.TenantId == tenantId &&
                        (x.ExpiresAtUtc == null || x.ExpiresAtUtc > utcNow) &&
                        ((x.ProviderName == PermissionProviderNames.User && x.ProviderKey == userKey) ||
                         (x.ProviderName == PermissionProviderNames.Role && roleKeys.Contains(x.ProviderKey))))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var userAllowExact = new HashSet<string>(StringComparer.Ordinal);
        var userDenyExact = new HashSet<string>(StringComparer.Ordinal);
        var userAllowWildcard = new HashSet<string>(StringComparer.Ordinal);
        var userDenyWildcard = new HashSet<string>(StringComparer.Ordinal);
        var roleAllowExact = new HashSet<string>(StringComparer.Ordinal);
        var roleDenyExact = new HashSet<string>(StringComparer.Ordinal);
        var roleAllowWildcard = new HashSet<string>(StringComparer.Ordinal);
        var roleDenyWildcard = new HashSet<string>(StringComparer.Ordinal);
        DateTime? nearestExpiryUtc = null;

        foreach (var grant in grants)
        {
            var name = grant.Name;
            var isWildcard = PermissionPatternHelper.IsWildcard(name);

            var isUser = grant.ProviderName == PermissionProviderNames.User;
            var isAllow = grant.Effect == PermissionGrantEffect.Allow;

            if (isUser)
            {
                Add(userAllowExact, userDenyExact, userAllowWildcard, userDenyWildcard, name, isWildcard, isAllow);
            }
            else
            {
                Add(roleAllowExact, roleDenyExact, roleAllowWildcard, roleDenyWildcard, name, isWildcard, isAllow);
            }

            if (grant.ExpiresAtUtc.HasValue && (!nearestExpiryUtc.HasValue || grant.ExpiresAtUtc.Value < nearestExpiryUtc.Value))
            {
                nearestExpiryUtc = grant.ExpiresAtUtc.Value;
            }
        }

        var snapshot = new PermissionSnapshotCacheItem
        {
            Stamp = combinedStamp,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UserAllowExact = userAllowExact.ToArray(),
            UserDenyExact = userDenyExact.ToArray(),
            UserAllowWildcard = userAllowWildcard.ToArray(),
            UserDenyWildcard = userDenyWildcard.ToArray(),
            RoleAllowExact = roleAllowExact.ToArray(),
            RoleDenyExact = roleDenyExact.ToArray(),
            RoleAllowWildcard = roleAllowWildcard.ToArray(),
            RoleDenyWildcard = roleDenyWildcard.ToArray()
        };

        return (snapshot, nearestExpiryUtc);
    }

    private static void Add(
        HashSet<string> allowExact,
        HashSet<string> denyExact,
        HashSet<string> allowWildcard,
        HashSet<string> denyWildcard,
        string name,
        bool isWildcard,
        bool isAllow)
    {
        if (isWildcard)
        {
            if (isAllow)
            {
                allowWildcard.Add(name);
                denyWildcard.Remove(name);
            }
            else
            {
                denyWildcard.Add(name);
                allowWildcard.Remove(name);
            }
        }
        else
        {
            if (isAllow)
            {
                allowExact.Add(name);
                denyExact.Remove(name);
            }
            else
            {
                denyExact.Add(name);
                allowExact.Remove(name);
            }
        }
    }

    private static long CombineStamp(long userStamp, List<long> roleStamps)
    {
        var max = userStamp;
        foreach (var stamp in roleStamps)
        {
            if (stamp > max)
            {
                max = stamp;
            }
        }
        return max;
    }
}
