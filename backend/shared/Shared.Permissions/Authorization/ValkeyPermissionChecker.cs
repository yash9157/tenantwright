using System.Security.Claims;
using Microsoft.Extensions.Caching.Hybrid;
using Permissions.Domain.Shared.Enums;
using Shared.Permissions.Caching;
using Shared.Permissions.Definitions;
using Microsoft.Extensions.Options;

namespace Shared.Permissions.Authorization;

public class ValkeyPermissionChecker : IPermissionChecker
{
    private static readonly HybridCacheEntryOptions StampCacheOptions = new()
    {
        Expiration = TimeSpan.FromHours(1)
    };

    private static readonly HybridCacheEntryOptions ProviderGrantsCacheOptions = new()
    {
        Expiration = TimeSpan.FromHours(1)
    };

    private static readonly HybridCacheEntryOptions SnapshotCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(30)
    };

    private static readonly HybridCacheEntryOptions DecisionCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(15)
    };

    private readonly PermissionProviderStampHybridCache _stampCache;
    private readonly PermissionProviderGrantsHybridCache _providerGrantsCache;
    private readonly PermissionSnapshotHybridCache _snapshotCache;
    private readonly PermissionDecisionHybridCache _decisionCache;
    private readonly TenantFeatureValueHybridCache _featureCache;
    private readonly SharedPermissionDefinitionIndex _definitions;
    private readonly RoleIdentityPermissionOptions _roleIdentityOptions;

    public ValkeyPermissionChecker(
        PermissionProviderStampHybridCache stampCache,
        PermissionProviderGrantsHybridCache providerGrantsCache,
        PermissionSnapshotHybridCache snapshotCache,
        PermissionDecisionHybridCache decisionCache,
        TenantFeatureValueHybridCache featureCache,
        SharedPermissionDefinitionIndex definitions,
        IOptions<RoleIdentityPermissionOptions> roleIdentityOptions)
    {
        _stampCache = stampCache;
        _providerGrantsCache = providerGrantsCache;
        _snapshotCache = snapshotCache;
        _decisionCache = decisionCache;
        _featureCache = featureCache;
        _definitions = definitions;
        _roleIdentityOptions = roleIdentityOptions.Value;
    }

    public async Task<bool> IsGrantedAsync(ClaimsPrincipal principal, string permissionName, CancellationToken cancellationToken = default)
    {
        var map = await IsGrantedAsync(principal, new[] { permissionName }, cancellationToken);
        return map.TryGetValue(permissionName.Trim(), out var granted) && granted;
    }

    public async Task<Dictionary<string, bool>> IsGrantedAsync(ClaimsPrincipal principal, IReadOnlyList<string> permissionNames, CancellationToken cancellationToken = default)
    {
        var userId = PermissionPrincipalAccessor.FindUserId(principal);
        if (!userId.HasValue)
        {
            return permissionNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(x => x, _ => false, StringComparer.Ordinal);
        }

        var tenantId = PermissionPrincipalAccessor.FindTenantId(principal);
        if (!Shared.Security.ClaimValueParser.TryParseCanonicalGuidClaims(
                principal.Claims
                    .Where(x => x.Type == Shared.Security.SharedClaimTypes.IdentityRoleId)
                    .Select(x => x.Value),
                _roleIdentityOptions.MaxActiveRoleMemberships,
                out var roleIds))
        {
            return permissionNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(x => x, _ => false, StringComparer.Ordinal);
        }

        var normalizedPermissions = permissionNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedPermissions.Length == 0)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        var userStamp = await GetStampAsync(tenantId, ProviderNames.User, userId.Value.ToString("D"), cancellationToken);
        var featureProviderKey = tenantId?.ToString("D") ?? "host";
        var featureStamp = await GetStampAsync(tenantId, ProviderNames.Feature, featureProviderKey, cancellationToken);

        var roleProviderKeys = roleIds.Select(x => x.ToString("D")).ToArray();

        var roleStampByProvider = new Dictionary<string, long>(StringComparer.Ordinal);
        var roleStamps = new List<long>(roleProviderKeys.Length);
        foreach (var providerKey in roleProviderKeys)
        {
            var stamp = await GetStampAsync(tenantId, ProviderNames.Role, providerKey, cancellationToken);
            roleStampByProvider[providerKey] = stamp;
            roleStamps.Add(stamp);
        }

        var combinedStamp = CombineStamp(userStamp, roleStamps, featureStamp);
        var rolesHash = PermissionPrincipalAccessor.ComputeRoleIdentityHash(roleIds);

        var snapshot = combinedStamp > 0
            ? await GetOrCreateSnapshotAsync(
                tenantId,
                userId.Value,
                rolesHash,
                userStamp,
                roleStampByProvider,
                roleProviderKeys,
                combinedStamp,
                cancellationToken)
            : await BuildSnapshotAsync(
                tenantId,
                userId.Value,
                userStamp,
                roleStampByProvider,
                roleProviderKeys,
                combinedStamp,
                cancellationToken);

        var decisions = new Dictionary<string, bool>(normalizedPermissions.Length, StringComparer.Ordinal);
        foreach (var permission in normalizedPermissions)
        {
            var decisionItem = combinedStamp > 0
                ? await GetOrCreateDecisionAsync(tenantId, userId.Value, rolesHash, combinedStamp, snapshot, permission, cancellationToken)
                : await EvaluateAsync(tenantId, snapshot, permission, cancellationToken);
            decisions[permission] = decisionItem?.IsGranted == true;
        }

        return decisions;
    }

    private async Task<long> GetStampAsync(Guid? tenantId, string providerName, string providerKey, CancellationToken cancellationToken)
    {
        var key = PermissionCacheKeys.BuildProviderKey(tenantId, providerName, providerKey);
        var cacheItem = await _stampCache.GetOrCreateAsync(
            key,
            () => Task.FromResult(new PermissionProviderStampCacheItem { Stamp = 0, UpdatedAtUtc = DateTimeOffset.UtcNow }),
            optionsFactory: () => StampCacheOptions,
            considerUow: false,
            token: cancellationToken);

        var stamp = cacheItem?.Stamp ?? 0;
        if (stamp <= 0)
        {
            await _stampCache.RemoveAsync(key, considerUow: false, token: cancellationToken);
        }

        return stamp;
    }

    private async Task<PermissionProviderGrantsCacheItem> GetProviderGrantsAsync(Guid? tenantId, string providerName, string providerKey, long stamp, CancellationToken cancellationToken)
    {
        if (stamp <= 0)
        {
            return new PermissionProviderGrantsCacheItem { UpdatedAtUtc = DateTimeOffset.UtcNow };
        }

        var key = PermissionCacheKeys.BuildProviderGrantsKey(tenantId, providerName, providerKey, stamp);
        var cacheItem = await _providerGrantsCache.GetOrCreateAsync(
            key,
            () => Task.FromResult(new PermissionProviderGrantsCacheItem { UpdatedAtUtc = DateTimeOffset.UtcNow }),
            optionsFactory: () => ProviderGrantsCacheOptions,
            considerUow: false,
            token: cancellationToken);

        return cacheItem ?? new PermissionProviderGrantsCacheItem();
    }

    private async Task<PermissionSnapshotCacheItem> GetOrCreateSnapshotAsync(
        Guid? tenantId,
        Guid userId,
        string rolesHash,
        long userStamp,
        IReadOnlyDictionary<string, long> roleStampByProvider,
        IReadOnlyList<string> roleProviderKeys,
        long combinedStamp,
        CancellationToken cancellationToken)
    {
        var snapshotKey = PermissionCacheKeys.BuildSnapshotKey(tenantId, userId, rolesHash, combinedStamp);
        var snapshot = await _snapshotCache.GetOrCreateAsync(
            snapshotKey,
            () => BuildSnapshotAsync(tenantId, userId, userStamp, roleStampByProvider, roleProviderKeys, combinedStamp, cancellationToken),
            optionsFactory: () => SnapshotCacheOptions,
            considerUow: false,
            token: cancellationToken);

        return snapshot ?? new PermissionSnapshotCacheItem
        {
            Stamp = combinedStamp,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private Task<PermissionDecisionCacheItem?> GetOrCreateDecisionAsync(
        Guid? tenantId,
        Guid userId,
        string rolesHash,
        long combinedStamp,
        PermissionSnapshotCacheItem snapshot,
        string permission,
        CancellationToken cancellationToken)
    {
        var decisionKey = PermissionCacheKeys.BuildDecisionKey(tenantId, userId, rolesHash, combinedStamp, permission);
        return _decisionCache.GetOrCreateAsync(
            decisionKey,
            () => EvaluateAsync(tenantId, snapshot, permission, cancellationToken),
            optionsFactory: () => DecisionCacheOptions,
            considerUow: false,
            token: cancellationToken);
    }

    private async Task<PermissionSnapshotCacheItem> BuildSnapshotAsync(
        Guid? tenantId,
        Guid userId,
        long userStamp,
        IReadOnlyDictionary<string, long> roleStampByProvider,
        IReadOnlyList<string> roleProviderKeys,
        long combinedStamp,
        CancellationToken cancellationToken)
    {
        var user = await GetProviderGrantsAsync(tenantId, ProviderNames.User, userId.ToString("D"), userStamp, cancellationToken);

        var roleAllowExact = new HashSet<string>(StringComparer.Ordinal);
        var roleDenyExact = new HashSet<string>(StringComparer.Ordinal);
        var roleAllowWildcard = new HashSet<string>(StringComparer.Ordinal);
        var roleDenyWildcard = new HashSet<string>(StringComparer.Ordinal);

        foreach (var providerKey in roleProviderKeys)
        {
            var stamp = roleStampByProvider.TryGetValue(providerKey, out var value) ? value : 0;
            var role = await GetProviderGrantsAsync(
                tenantId,
                ProviderNames.Role,
                providerKey,
                stamp,
                cancellationToken);
            foreach (var permission in role.AllowExact) roleAllowExact.Add(permission);
            foreach (var permission in role.DenyExact) roleDenyExact.Add(permission);
            foreach (var permission in role.AllowWildcard) roleAllowWildcard.Add(permission);
            foreach (var permission in role.DenyWildcard) roleDenyWildcard.Add(permission);
        }

        return new PermissionSnapshotCacheItem
        {
            Stamp = combinedStamp,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UserAllowExact = user.AllowExact,
            UserDenyExact = user.DenyExact,
            UserAllowWildcard = user.AllowWildcard,
            UserDenyWildcard = user.DenyWildcard,
            RoleAllowExact = roleAllowExact.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            RoleDenyExact = roleDenyExact.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            RoleAllowWildcard = roleAllowWildcard.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            RoleDenyWildcard = roleDenyWildcard.OrderBy(x => x, StringComparer.Ordinal).ToArray()
        };
    }

    private async Task<PermissionDecisionCacheItem> EvaluateAsync(Guid? tenantId, PermissionSnapshotCacheItem snapshot, string permissionName, CancellationToken cancellationToken)
    {
        permissionName = permissionName.Trim();

        var decision = EvaluateByGrants(snapshot, permissionName);
        if (!decision.IsGranted)
        {
            return decision;
        }

        if (_definitions.TryGet(permissionName, out var def) && def.RequiredFeatures.Count > 0)
        {
            foreach (var required in def.RequiredFeatures)
            {
                if (string.IsNullOrWhiteSpace(required))
                {
                    continue;
                }

                var enabled = await IsTenantFeatureEnabledAsync(tenantId, required.Trim(), cancellationToken);
                if (!enabled)
                {
                    return Decision(snapshot.Stamp, false, PermissionDecisionSource.FeatureDenied);
                }
            }
        }

        return decision;
    }

    private static PermissionDecisionCacheItem EvaluateByGrants(PermissionSnapshotCacheItem snapshot, string permissionName)
    {
        if (Array.IndexOf(snapshot.UserDenyExact, permissionName) >= 0)
        {
            return Decision(snapshot.Stamp, false, PermissionDecisionSource.UserExplicitDeny);
        }

        if (MatchesAny(snapshot.UserDenyWildcard, permissionName))
        {
            return Decision(snapshot.Stamp, false, PermissionDecisionSource.WildcardDeny);
        }

        if (Array.IndexOf(snapshot.UserAllowExact, permissionName) >= 0)
        {
            return Decision(snapshot.Stamp, true, PermissionDecisionSource.UserExplicitAllow);
        }

        if (MatchesAny(snapshot.UserAllowWildcard, permissionName))
        {
            return Decision(snapshot.Stamp, true, PermissionDecisionSource.WildcardAllow);
        }

        if (Array.IndexOf(snapshot.RoleDenyExact, permissionName) >= 0)
        {
            return Decision(snapshot.Stamp, false, PermissionDecisionSource.RoleExplicitDeny);
        }

        if (MatchesAny(snapshot.RoleDenyWildcard, permissionName))
        {
            return Decision(snapshot.Stamp, false, PermissionDecisionSource.WildcardDeny);
        }

        if (Array.IndexOf(snapshot.RoleAllowExact, permissionName) >= 0)
        {
            return Decision(snapshot.Stamp, true, PermissionDecisionSource.RoleExplicitAllow);
        }

        if (MatchesAny(snapshot.RoleAllowWildcard, permissionName))
        {
            return Decision(snapshot.Stamp, true, PermissionDecisionSource.WildcardAllow);
        }

        foreach (var parent in PermissionPatternHelper.EnumerateParents(permissionName))
        {
            if (Array.IndexOf(snapshot.UserDenyExact, parent) >= 0 || Array.IndexOf(snapshot.RoleDenyExact, parent) >= 0)
            {
                return Decision(snapshot.Stamp, false, PermissionDecisionSource.WildcardDeny);
            }

            if (Array.IndexOf(snapshot.UserAllowExact, parent) >= 0 || Array.IndexOf(snapshot.RoleAllowExact, parent) >= 0)
            {
                return Decision(snapshot.Stamp, true, PermissionDecisionSource.InheritedAllow);
            }

            if (MatchesAny(snapshot.UserAllowWildcard, $"{parent}.*") || MatchesAny(snapshot.RoleAllowWildcard, $"{parent}.*"))
            {
                return Decision(snapshot.Stamp, true, PermissionDecisionSource.InheritedAllow);
            }
        }

        return Decision(snapshot.Stamp, false, PermissionDecisionSource.NotGranted);
    }

    private async Task<bool> IsTenantFeatureEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken)
    {
        var key = PermissionCacheKeys.BuildTenantFeatureKey(tenantId, featureName);
        var cacheItem = await _featureCache.GetOrCreateAsync(
            key,
            () => Task.FromResult(new FeatureValueCacheItem { UpdatedAtUtc = DateTimeOffset.UtcNow, Value = "false" }),
            optionsFactory: () => ProviderGrantsCacheOptions,
            considerUow: false,
            token: cancellationToken);

        var value = cacheItem?.Value ?? "false";
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    private static PermissionDecisionCacheItem Decision(long stamp, bool granted, PermissionDecisionSource source)
    {
        return new PermissionDecisionCacheItem
        {
            Stamp = stamp,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsGranted = granted,
            Source = (int)source
        };
    }

    private static bool MatchesAny(string[] patterns, string permissionName)
    {
        foreach (var pattern in patterns)
        {
            if (PermissionPatternHelper.Matches(permissionName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static long CombineStamp(long userStamp, List<long> roleStamps, long featureStamp)
    {
        var max = userStamp;
        foreach (var stamp in roleStamps)
        {
            if (stamp > max)
            {
                max = stamp;
            }
        }
        if (featureStamp > max)
        {
            max = featureStamp;
        }
        return max;
    }

    private static class ProviderNames
    {
        public const string User = "User";
        public const string Role = "Role";
        public const string Feature = "Feature";
    }
}
