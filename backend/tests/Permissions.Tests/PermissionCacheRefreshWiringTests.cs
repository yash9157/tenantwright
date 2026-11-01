using Permissions.Application.Caching;
using Permissions.Application.Evaluation;
using Shared.BackgroundTasks.Jobs;
using Shared.EntityFrameworkCore.TenantDatabases;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionCacheRefreshWiringTests
{
    [Fact]
    public void Cache_refresh_job_args_should_use_stable_job_name()
    {
        Assert.Equal("permissions.cache-refresh", BackgroundJobNameAttribute.GetName(typeof(PermissionCacheRefreshJobArgs)));
    }

    [Fact]
    public void Cache_refresh_options_should_normalize_missing_schedule_and_queue()
    {
        var options = new PermissionCacheRefreshOptions
        {
            RecurringCron = " ",
            Queue = " "
        };

        PermissionCacheRefreshOptionsPostConfigure.Normalize(options);

        Assert.Equal("*/30 * * * *", options.RecurringCron);
        Assert.Equal("permissions-recurring", options.Queue);
    }

    [Fact]
    public void Snapshot_builder_should_use_provider_cache_repair_service()
    {
        var constructor = Assert.Single(typeof(PermissionSnapshotBuilder).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Contains(parameters, x =>
            x.ParameterType == typeof(PermissionProviderCacheRepairService) &&
            !x.IsOptional);
        Assert.DoesNotContain(parameters, x => x.ParameterType == typeof(PermissionStampService));
    }

    [Fact]
    public void Cache_seed_should_use_provider_cache_repair_service()
    {
        var constructor = Assert.Single(typeof(PermissionCacheSeedService).GetConstructors());

        // The seed rebuilds from every Permissions database, exactly like the refresh job.
        Assert.Equal(
            [typeof(PermissionProviderCacheRepairService), typeof(TenantDatabaseFanOut)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }
}
