using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Caching;
using Shared.Caching.Abstractions;
using Shared.Caching.Implementations;
using Shared.Modularity.DependencyInjection;
using Shared.Threading;
using Shared.Uow.Abstractions;

namespace Shared.Permissions.Caching;

public class PermissionProviderStampHybridCache
    : SharedHybridCache<PermissionProviderStampCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "PermissionProviderStamp";

    public PermissionProviderStampHybridCache(
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

public class PermissionDecisionHybridCache
    : SharedHybridCache<PermissionDecisionCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "PermissionDecision";

    public PermissionDecisionHybridCache(
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

public class PermissionProviderGrantsHybridCache
    : SharedHybridCache<PermissionProviderGrantsCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "PermissionProviderGrants";

    public PermissionProviderGrantsHybridCache(
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

public class PermissionSnapshotHybridCache
    : SharedHybridCache<PermissionSnapshotCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "PermissionSnapshot";

    public PermissionSnapshotHybridCache(
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

public class TenantFeatureValueHybridCache
    : SharedHybridCache<FeatureValueCacheItem, string>,
        ISingletonDependency
{
    public const string Name = "TenantFeatureValue";

    public TenantFeatureValueHybridCache(
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
