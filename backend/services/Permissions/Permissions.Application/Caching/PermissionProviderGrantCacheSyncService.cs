using Microsoft.EntityFrameworkCore;
using Permissions.Application.Evaluation;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Caching;

public class PermissionProviderGrantCacheSyncService : ITransientDependency
{
    private static readonly Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromHours(1)
    };

    private readonly IRepository<PermissionGrant, Guid> _grantRepository;
    private readonly PermissionProviderGrantsCache _grantsCache;
    private readonly RolePrivilegeMatrixCache _rolePrivilegeMatrixCache;

    public PermissionProviderGrantCacheSyncService(
        IRepository<PermissionGrant, Guid> grantRepository,
        PermissionProviderGrantsCache grantsCache,
        RolePrivilegeMatrixCache rolePrivilegeMatrixCache)
    {
        _grantRepository = grantRepository;
        _grantsCache = grantsCache;
        _rolePrivilegeMatrixCache = rolePrivilegeMatrixCache;
    }

    public async Task SyncAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        long stamp,
        bool considerUow = true,
        CancellationToken cancellationToken = default)
    {
        providerName = providerName.Trim();
        providerKey = providerKey.Trim();

        var queryable = await _grantRepository.GetQueryableAsync();
        var grants = await queryable
            .Where(x => x.TenantId == tenantId && x.ProviderName == providerName && x.ProviderKey == providerKey)
            .AsNoTracking()
            .Select(x => new { x.Name, x.Effect })
            .ToListAsync(cancellationToken);

        var allowExact = new HashSet<string>(StringComparer.Ordinal);
        var denyExact = new HashSet<string>(StringComparer.Ordinal);
        var allowWildcard = new HashSet<string>(StringComparer.Ordinal);
        var denyWildcard = new HashSet<string>(StringComparer.Ordinal);

        foreach (var g in grants)
        {
            var name = g.Name.Trim();
            var isWildcard = PermissionPatternHelper.IsWildcard(name);

            if (g.Effect == PermissionGrantEffect.Allow)
            {
                if (isWildcard) allowWildcard.Add(name);
                else allowExact.Add(name);
            }
            else
            {
                if (isWildcard) denyWildcard.Add(name);
                else denyExact.Add(name);
            }
        }

        var key = PermissionCacheKeys.BuildProviderGrantsKey(tenantId, providerName, providerKey, stamp);
        await _grantsCache.SetAsync(
            key,
            new PermissionProviderGrantsCacheItem
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                AllowExact = allowExact.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                DenyExact = denyExact.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                AllowWildcard = allowWildcard.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                DenyWildcard = denyWildcard.OrderBy(x => x, StringComparer.Ordinal).ToArray()
            },
            options: CacheOptions,
            considerUow: considerUow,
            token: cancellationToken);

        if (tenantId.HasValue && string.Equals(providerName, PermissionProviderNames.Role, StringComparison.Ordinal))
        {
            await _rolePrivilegeMatrixCache.RemoveAsync(
                PermissionCacheKeys.BuildRolePrivilegeMatrixKey(tenantId.Value),
                considerUow: considerUow,
                token: cancellationToken);
        }
    }
}
