using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shared.BackgroundTasks.Jobs;
using Shared.BackgroundTasks.Workers;
using Shared.DistributedLocking;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.MultiTenancy;
using Shared.Uow;
using StackExchange.Redis;

namespace Shared.BackgroundTasks;

[DependsOn(typeof(MultiTenancyModule))]
[DependsOn(typeof(DistributedLockingModule))]
[DependsOn(typeof(UowModule))]
public sealed class SharedBackgroundTasksModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.Configure<BackgroundTasksOptions>(
            context.Configuration.GetSection(BackgroundTasksOptions.SectionName));
        context.Services.PostConfigure<BackgroundTasksOptions>(BackgroundTasksOptionsPostConfigure.Normalize);

        context.Services.TryAddSingleton<IBackgroundJobRegistry, BackgroundJobRegistry>();
        context.Services.TryAddSingleton<IBackgroundJobSerializer, SystemTextJsonBackgroundJobSerializer>();
        context.Services.TryAddTransient<IBackgroundJobExecutor, BackgroundJobExecutor>();
        context.Services.TryAddTransient<IBackgroundJobManager, DisabledBackgroundJobManager>();
        context.Services.TryAddTransient<IRecurringBackgroundJobManager, DisabledRecurringBackgroundJobManager>();
        context.Services.TryAddSingleton<IBackgroundWorkerWakeSignal>(serviceProvider =>
            new BackgroundWorkerWakeSignal(
                serviceProvider.GetService<ILogger<BackgroundWorkerWakeSignal>>(),
                serviceProvider.GetService<IConnectionMultiplexer>()));
    }
}
