using Permissions.Domain;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.DependencyInjection;
using Shared.EntityFrameworkCore.ExceptionTranslation;
using Shared.EntityFrameworkCore.Migrations;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;

namespace Permissions.EntityFrameworkCore;

[DependsOn(typeof(PermissionsDomainModule))]
[DependsOn(typeof(SharedEntityFrameworkCoreModule))]
public class PermissionsEntityFrameworkCoreModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Configuration;

        context.Services.Configure<SharedDatabaseExceptionTranslationOptions>(options =>
        {
            options.Configure<PermissionsDbContextApp>(
                "UX_AccessReviewCampaigns_OneActivePerTenant",
                new SharedDatabaseConstraintDescriptor(
                    "active_access_review_campaign_exists",
                    "Close the active access review campaign before opening a new one."));
        });

        context.Services.AddDbContext<PermissionsDbContextApp>(options =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"));
            options.AddSharedDbContextOptionsExtension();
        });

        context.Services.AddSharedEfCoreAutoMigration<PermissionsDbContextApp>();

        var repositoryOptions = new SharedDbContextRegistrationOptions(typeof(PermissionsDbContextApp), context.Services);
        repositoryOptions.AddDefaultRepositories(includeAllEntities: true);
        new EfCoreRepositoryRegistrar(repositoryOptions).AddRepositories();

    }
}
