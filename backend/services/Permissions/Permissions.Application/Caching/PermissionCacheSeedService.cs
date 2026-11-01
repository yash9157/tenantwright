using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Caching;

public class PermissionCacheSeedService : ITransientDependency
{
    private const string Work = "permissions-cache-seed";

    private readonly PermissionProviderCacheRepairService _repairService;
    private readonly TenantDatabaseFanOut _fanOut;

    public PermissionCacheSeedService(PermissionProviderCacheRepairService repairService, TenantDatabaseFanOut fanOut)
    {
        _repairService = repairService;
        _fanOut = fanOut;
    }

    /// <summary>
    /// Seeds the provider cache from every Permissions database, like the refresh job: grants of a tenant with a
    /// dedicated database live only there.
    /// </summary>
    public Task SeedAsync(CancellationToken cancellationToken = default) =>
        _fanOut.ForEachDatabaseAsync<PermissionsDbContextApp>(
            Work,
            (_, token) => _repairService.RefreshAllProvidersAsync(token),
            cancellationToken);
}
