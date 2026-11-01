using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Permissions.Application.Contracts.AccessReviews;
using Shared.Caching;
using Shared.Caching.Abstractions;
using Shared.Caching.Implementations;
using Shared.Modularity.DependencyInjection;
using Shared.Threading;
using Shared.Uow.Abstractions;

namespace Permissions.Application.Caching;

public sealed class RolePrivilegeMatrixCache
    : SharedHybridCache<RolePrivilegeMatrixDto, string>, ISingletonDependency
{
    public const string Name = "PermissionRolePrivilegeMatrix";

    public RolePrivilegeMatrixCache(
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
