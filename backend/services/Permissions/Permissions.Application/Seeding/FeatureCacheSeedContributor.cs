using Permissions.Application.Caching;
using Shared.Data.Seeding;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Seeding;

public class FeatureCacheSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly FeatureCacheSeedService _featureCacheSeedService;

    public FeatureCacheSeedContributor(FeatureCacheSeedService featureCacheSeedService)
    {
        _featureCacheSeedService = featureCacheSeedService;
    }

    public Task SeedAsync(DataSeedContext context)
    {
        return _featureCacheSeedService.SeedAsync();
    }
}
