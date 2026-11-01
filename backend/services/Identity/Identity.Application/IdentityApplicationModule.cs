using Identity.Application.Contracts;
using Identity.Domain;
using Identity.Application.Users;
using Shared.BackgroundTasks;
using Shared.BackgroundTasks.DependencyInjection;
using Shared.Caching;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;

namespace Identity.Application;

[DependsOn(typeof(IdentityDomainModule))]
[DependsOn(typeof(IdentityApplicationContractsModule))]
[DependsOn(typeof(CachingModule))]
[DependsOn(typeof(SharedBackgroundTasksModule))]
public class IdentityApplicationModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddBackgroundJob<IdentityUserExportBackgroundJob, IdentityUserExportJobArgs>();
        context.Services.AddBackgroundJob<IdentityUserImportBackgroundJob, IdentityUserImportJobArgs>();
    }
}
