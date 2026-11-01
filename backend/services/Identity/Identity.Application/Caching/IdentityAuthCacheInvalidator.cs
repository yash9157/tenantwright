using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Caching;
using Microsoft.Extensions.Logging;
using Shared.Caching.Abstractions;
using Shared.Caching.Implementations;
using Shared.Modularity.DependencyInjection;

namespace Identity.Application.Caching;

public class IdentityAuthCacheInvalidator : IIdentityAuthCacheInvalidator, ITransientDependency
{
    private readonly Shared.Caching.Abstractions.IDistributedCache _distributedCache;
    private readonly IDistributedCacheKeyNormalizer _keyNormalizer;
    private readonly ILogger<IdentityAuthCacheInvalidator> _logger;

    public IdentityAuthCacheInvalidator(
        Shared.Caching.Abstractions.IDistributedCache distributedCache,
        IDistributedCacheKeyNormalizer keyNormalizer,
        ILogger<IdentityAuthCacheInvalidator> logger)
    {
        _distributedCache = distributedCache;
        _keyNormalizer = keyNormalizer;
        _logger = logger;
    }

    public async Task InvalidateProfileAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return;
        }

        var logical = IdentityUserCacheContracts.BuildProfileLogicalKey(tenantId, userId);
        await RemoveAsync(IdentityUserCacheContracts.ProfileCacheName, logical, cancellationToken);
    }

    public async Task InvalidateAuthorizationAsync(Guid? tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return;
        }

        var logical = IdentityUserCacheContracts.BuildAuthorizationLogicalKey(tenantId, userId);
        await RemoveAsync(IdentityUserCacheContracts.AuthorizationCacheName, logical, cancellationToken);
    }

    public async Task InvalidateAliasesAsync(Guid? tenantId, IEnumerable<string> aliasHashes, CancellationToken cancellationToken = default)
    {
        var hashes = aliasHashes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var hash in hashes)
        {
            var logical = IdentityUserCacheContracts.BuildAliasLogicalKeyByHash(tenantId, hash);
            await RemoveAsync(IdentityUserCacheContracts.AliasCacheName, logical, cancellationToken);
        }
    }

    private async Task RemoveAsync(string cacheName, string logicalKey, CancellationToken cancellationToken)
    {
        var normalized = _keyNormalizer.NormalizeKey(
            new DistributedCacheKeyNormalizeArgs(logicalKey, cacheName, ignoreMultiTenancy: false));

        await _distributedCache.RemoveAsync(normalized, cancellationToken);

        _logger.LogDebug(
            "Removed identity cache entry from Valkey. CacheName={CacheName}, Key={LogicalKey}",
            cacheName,
            logicalKey);
    }
}
