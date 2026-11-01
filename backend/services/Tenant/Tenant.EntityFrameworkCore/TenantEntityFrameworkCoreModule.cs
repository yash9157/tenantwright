using Tenant.Domain;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.DependencyInjection;
using Shared.EntityFrameworkCore.ExceptionTranslation;
using Shared.EntityFrameworkCore.Migrations;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Tenant.Domain.Shared;

namespace Tenant.EntityFrameworkCore;

[DependsOn(typeof(TenantDomainModule))]
[DependsOn(typeof(SharedEntityFrameworkCoreModule))]
public class TenantEntityFrameworkCoreModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Configuration;
        context.Services.Configure<SharedDatabaseExceptionTranslationOptions>(options =>
        {
            options.Configure<TenantDbContextApp>(
                TenantDbContextApp.TenantConnectionStringTargetIndexName,
                new SharedDatabaseConstraintDescriptor(
                    TenantErrorCodes.DedicatedDatabaseTargetInUse,
                    "The database is already assigned as another dedicated database."));
        });

        context.Services.AddDbContext<TenantDbContextApp>(options =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"));
            options.AddSharedDbContextOptionsExtension();
        });

        context.Services.AddSharedEfCoreAutoMigration<TenantDbContextApp>();
        var repositoryOptions = new SharedDbContextRegistrationOptions(typeof(TenantDbContextApp), context.Services);
        repositoryOptions.AddDefaultRepositories(includeAllEntities: true);
        new EfCoreRepositoryRegistrar(repositoryOptions).AddRepositories();
    }
}
