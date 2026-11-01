using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.DependencyInjection;
using Shared.EntityFrameworkCore.Migrations;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Workflows.EntityFrameworkCore.EntityFrameworkCore;
namespace Workflows.EntityFrameworkCore;

[DependsOn(typeof(SharedEntityFrameworkCoreModule))]
public class WorkflowsEntityFrameworkCoreModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Configuration;
        context.Services.AddDbContext<WorkflowsDbContextApp>(options =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"));
            options.AddSharedDbContextOptionsExtension();
        });
        context.Services.AddSharedEfCoreAutoMigration<WorkflowsDbContextApp>();
        var repositoryOptions = new SharedDbContextRegistrationOptions(typeof(WorkflowsDbContextApp), context.Services);
        repositoryOptions.AddDefaultRepositories(includeAllEntities: true);
        new EfCoreRepositoryRegistrar(repositoryOptions).AddRepositories();
    }
}
