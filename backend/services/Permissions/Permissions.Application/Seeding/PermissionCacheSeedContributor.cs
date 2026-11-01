using Permissions.Application.Caching;
using Shared.Data.Seeding;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Seeding;

public class PermissionCacheSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly PermissionCacheSeedService _permissionCacheSeedService;

    public PermissionCacheSeedContributor(PermissionCacheSeedService permissionCacheSeedService)
    {
        _permissionCacheSeedService = permissionCacheSeedService;
    }

    public Task SeedAsync(DataSeedContext context)
    {
        return _permissionCacheSeedService.SeedAsync();
    }
}
