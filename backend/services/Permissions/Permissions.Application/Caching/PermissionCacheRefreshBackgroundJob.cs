using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.BackgroundTasks.Jobs;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Uow.Attributes;

namespace Permissions.Application.Caching;

internal sealed class PermissionCacheRefreshBackgroundJob : IBackgroundJob<PermissionCacheRefreshJobArgs>
{
    private const string Work = "permissions-cache-refresh";

    private readonly PermissionProviderCacheRepairService _repairService;
    private readonly TenantDatabaseFanOut _fanOut;

    public PermissionCacheRefreshBackgroundJob(
        PermissionProviderCacheRepairService repairService,
        TenantDatabaseFanOut fanOut)
    {
        _repairService = repairService;
        _fanOut = fanOut;
    }

    /// <summary>
    /// Rebuilds the provider cache from every Permissions database: grants of a tenant with a dedicated database
    /// live only there.
    /// </summary>
    [UnitOfWork(isTransactional: false)]
    public Task ExecuteAsync(BackgroundJobExecutionContext<PermissionCacheRefreshJobArgs> context) =>
        _fanOut.ForEachDatabaseAsync<PermissionsDbContextApp>(
            Work,
            (_, cancellationToken) => _repairService.RefreshAllProvidersAsync(cancellationToken),
            context.CancellationToken);
}
