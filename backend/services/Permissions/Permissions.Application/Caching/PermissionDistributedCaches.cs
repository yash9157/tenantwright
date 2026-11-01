using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Caching;
using Shared.Caching.Abstractions;
using Shared.Caching.Implementations;
using Shared.Modularity.DependencyInjection;
using Shared.Threading;
using Shared.Uow.Abstractions;

namespace Permissions.Application.Caching;

public class PermissionProviderStampCache
    : SharedHybridCache<PermissionProviderStampCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "PermissionProviderStamp";

    public PermissionProviderStampCache(
        IServiceProvider serviceProvider,
        IOptions<SharedHybridCacheOptions> hybridCacheOption,
        HybridCache hybridCache,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache distributedCache,
        ICancellationTokenProvider cancellationTokenProvider,
        IDistributedCacheKeyNormalizer keyNormalizer,
        IServiceScopeFactory serviceScopeFactory,
        IUnitOfWorkManager unitOfWorkManager)
        : base(
            serviceProvider,
            hybridCacheOption,
            hybridCache,
            distributedCache,
            cancellationTokenProvider,
            keyNormalizer,
            serviceScopeFactory,
            unitOfWorkManager)
    {
        CacheName = Name;
        IgnoreMultiTenancy = true;
    }
}

public class PermissionSnapshotCache
    : SharedHybridCache<PermissionSnapshotCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "PermissionSnapshot";

    public PermissionSnapshotCache(
        IServiceProvider serviceProvider,
        IOptions<SharedHybridCacheOptions> hybridCacheOption,
        HybridCache hybridCache,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache distributedCache,
        ICancellationTokenProvider cancellationTokenProvider,
        IDistributedCacheKeyNormalizer keyNormalizer,
        IServiceScopeFactory serviceScopeFactory,
        IUnitOfWorkManager unitOfWorkManager)
        : base(
            serviceProvider,
            hybridCacheOption,
            hybridCache,
            distributedCache,
            cancellationTokenProvider,
            keyNormalizer,
            serviceScopeFactory,
            unitOfWorkManager)
    {
        CacheName = Name;
        IgnoreMultiTenancy = true;
    }
}

public class PermissionProviderGrantsCache
    : SharedHybridCache<PermissionProviderGrantsCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "PermissionProviderGrants";

    public PermissionProviderGrantsCache(
        IServiceProvider serviceProvider,
        IOptions<SharedHybridCacheOptions> hybridCacheOption,
        HybridCache hybridCache,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache distributedCache,
        ICancellationTokenProvider cancellationTokenProvider,
        IDistributedCacheKeyNormalizer keyNormalizer,
        IServiceScopeFactory serviceScopeFactory,
        IUnitOfWorkManager unitOfWorkManager)
        : base(
            serviceProvider,
            hybridCacheOption,
            hybridCache,
            distributedCache,
            cancellationTokenProvider,
            keyNormalizer,
            serviceScopeFactory,
            unitOfWorkManager)
    {
        CacheName = Name;
        IgnoreMultiTenancy = true;
    }
}

public class TenantFeatureValueCache
    : SharedHybridCache<FeatureValueCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "TenantFeatureValue";

    public TenantFeatureValueCache(
        IServiceProvider serviceProvider,
        IOptions<SharedHybridCacheOptions> hybridCacheOption,
        HybridCache hybridCache,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache distributedCache,
        ICancellationTokenProvider cancellationTokenProvider,
        IDistributedCacheKeyNormalizer keyNormalizer,
        IServiceScopeFactory serviceScopeFactory,
        IUnitOfWorkManager unitOfWorkManager)
        : base(
            serviceProvider,
            hybridCacheOption,
            hybridCache,
            distributedCache,
            cancellationTokenProvider,
            keyNormalizer,
            serviceScopeFactory,
            unitOfWorkManager)
    {
        CacheName = Name;
        IgnoreMultiTenancy = true;
    }
}
