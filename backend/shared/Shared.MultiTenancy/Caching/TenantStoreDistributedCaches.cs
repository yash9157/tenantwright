using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Shared.Caching.Abstractions;
using Shared.Caching;
using Shared.Caching.Implementations;
using Shared.Modularity.DependencyInjection;
using Shared.Threading;
using Shared.Uow.Abstractions;

namespace Shared.MultiTenancy.Caching;

public class TenantByIdCache
    : DistributedCache<TenantCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "TenantById";

    public TenantByIdCache(
        IOptions<SharedDistributedCacheOptions> distributedCacheOption,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache cache,
        ICancellationTokenProvider cancellationTokenProvider,
        IDistributedCacheSerializer serializer,
        IDistributedCacheKeyNormalizer keyNormalizer,
        IServiceScopeFactory serviceScopeFactory,
        IUnitOfWorkManager unitOfWorkManager)
        : base(
            distributedCacheOption,
            cache,
            cancellationTokenProvider,
            serializer,
            keyNormalizer,
            serviceScopeFactory,
            unitOfWorkManager)
    {
        IgnoreMultiTenancy = true;
        CacheName = Name;
    }
}

public class TenantByNameCache
    : DistributedCache<TenantByNameCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "TenantByName";

    public TenantByNameCache(
        IOptions<SharedDistributedCacheOptions> distributedCacheOption,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache cache,
        ICancellationTokenProvider cancellationTokenProvider,
        IDistributedCacheSerializer serializer,
        IDistributedCacheKeyNormalizer keyNormalizer,
        IServiceScopeFactory serviceScopeFactory,
        IUnitOfWorkManager unitOfWorkManager)
        : base(
            distributedCacheOption,
            cache,
            cancellationTokenProvider,
            serializer,
            keyNormalizer,
            serviceScopeFactory,
            unitOfWorkManager)
    {
        IgnoreMultiTenancy = true;
        CacheName = Name;
    }
}

public class TenantListCache
    : DistributedCache<TenantListCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "TenantList";

    public TenantListCache(
        IOptions<SharedDistributedCacheOptions> distributedCacheOption,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache cache,
        ICancellationTokenProvider cancellationTokenProvider,
        IDistributedCacheSerializer serializer,
        IDistributedCacheKeyNormalizer keyNormalizer,
        IServiceScopeFactory serviceScopeFactory,
        IUnitOfWorkManager unitOfWorkManager)
        : base(
            distributedCacheOption,
            cache,
            cancellationTokenProvider,
            serializer,
            keyNormalizer,
            serviceScopeFactory,
            unitOfWorkManager)
    {
        IgnoreMultiTenancy = true;
        CacheName = Name;
    }
}

public class TenantListStampCache
    : DistributedCache<TenantListStampCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "TenantListStamp";

    public TenantListStampCache(
        IOptions<SharedDistributedCacheOptions> distributedCacheOption,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache cache,
        ICancellationTokenProvider cancellationTokenProvider,
        IDistributedCacheSerializer serializer,
        IDistributedCacheKeyNormalizer keyNormalizer,
        IServiceScopeFactory serviceScopeFactory,
        IUnitOfWorkManager unitOfWorkManager)
        : base(
            distributedCacheOption,
            cache,
            cancellationTokenProvider,
            serializer,
            keyNormalizer,
            serviceScopeFactory,
            unitOfWorkManager)
    {
        IgnoreMultiTenancy = true;
        CacheName = Name;
    }
}
