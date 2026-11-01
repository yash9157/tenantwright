using Identity.Domain;
using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.EntityFrameworkCore.Stores;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.DependencyInjection;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.EntityFrameworkCore.Migrations;
using Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;
using Shared.Uow.Abstractions;

namespace Identity.EntityFrameworkCore;

[DependsOn(typeof(IdentityDomainModule))]
[DependsOn(typeof(SharedEntityFrameworkCoreModule))]
public class IdentityEntityFrameworkCoreModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Configuration;

        context.Services.AddDbContext<IdentityDbContextApp>(options =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"));
            options.AddSharedDbContextOptionsExtension();
        });
        context.Services.AddScoped<IPersistenceBoundaryInitializer, EfCorePersistenceBoundaryInitializer<IdentityDbContextApp>>();

        context.Services.AddSharedEfCoreAutoMigration<IdentityDbContextApp>();
        var repositoryOptions = new SharedDbContextRegistrationOptions(typeof(IdentityDbContextApp), context.Services);
        repositoryOptions.AddDefaultRepositories(includeAllEntities: true);
        new EfCoreRepositoryRegistrar(repositoryOptions).AddRepositories();

        context.Services.AddIdentity<ApplicationUser, ApplicationRole>()
            .AddUserStore<EfApplicationUserStore>()
            .AddRoleStore<EfApplicationRoleStore>()
            .AddDefaultTokenProviders();
    }
}
