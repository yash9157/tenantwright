using Permissions.Application.Definitions;
using Shared.Data.Seeding;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Seeding;

public class DefinitionSyncSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly PermissionDefinitionSyncService _definitionSyncService;

    public DefinitionSyncSeedContributor(PermissionDefinitionSyncService definitionSyncService)
    {
        _definitionSyncService = definitionSyncService;
    }

    public Task SeedAsync(DataSeedContext context)
    {
        return _definitionSyncService.SyncAsync();
    }
}
