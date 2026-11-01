using Microsoft.Extensions.Options;
using Shared.Caching.Abstractions;

namespace Shared.Caching.Implementations;

public class DefaultDistributedCacheKeyNormalizer : IDistributedCacheKeyNormalizer
{
    private readonly ICacheTenantResolver _cacheTenantResolver;
    private readonly SharedDistributedCacheOptions _distributedCacheOptions;

    public DefaultDistributedCacheKeyNormalizer(
        ICacheTenantResolver cacheTenantResolver,
        IOptions<SharedDistributedCacheOptions> distributedCacheOptions)
    {
        _cacheTenantResolver = cacheTenantResolver;
        _distributedCacheOptions = distributedCacheOptions.Value;
    }

    public virtual string NormalizeKey(DistributedCacheKeyNormalizeArgs args)
    {
        var keyPrefix = _distributedCacheOptions.KeyPrefix ?? string.Empty;
        var normalizedKey = $"c:{args.CacheName},k:{keyPrefix}{args.Key}";

        var tenantId = _cacheTenantResolver.GetTenantIdOrNull();
        if (!args.IgnoreMultiTenancy && tenantId.HasValue)
        {
            normalizedKey = $"t:{tenantId:D},{normalizedKey}";
        }

        return normalizedKey;
    }
}
