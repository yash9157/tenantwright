using Notifications.Domain;
using Notifications.Domain.Notifications;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.DependencyInjection;
using Shared.EntityFrameworkCore.ExceptionTranslation;
using Shared.EntityFrameworkCore.Migrations;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;

namespace Notifications.EntityFrameworkCore;

[DependsOn(typeof(NotificationsDomainModule))]
[DependsOn(typeof(SharedEntityFrameworkCoreModule))]
public class NotificationsEntityFrameworkCoreModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Configuration;

        context.Services.Configure<SharedDatabaseExceptionTranslationOptions>(options =>
        {
            options.Configure<NotificationsDbContextApp>(
                "IX_Notifications_TenantId_RequestId",
                new SharedDatabaseConstraintDescriptor(
                    "notification_request_duplicate",
                    "This notification request was already accepted."));
            options.Configure<NotificationsDbContextApp>(
                "IX_NotificationProviderConfigs_ScopeKey_Channel_Name",
                new SharedDatabaseConstraintDescriptor(
                    "notification_provider_config_name_conflict",
                    "A notification provider configuration with this channel and name already exists."));
            options.Configure<NotificationsDbContextApp>(
                "IX_NotificationProviderConfigs_ScopeKey_Channel_IsDefault_Unique",
                new SharedDatabaseConstraintDescriptor(
                    "notification_provider_config_default_conflict",
                    "A default notification provider configuration already exists for this channel."));
        });

        context.Services.AddDbContext<NotificationsDbContextApp>(options =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"));
            options.AddSharedDbContextOptionsExtension();
        });

        context.Services.AddSharedEfCoreAutoMigration<NotificationsDbContextApp>();

        var repositoryOptions = new SharedDbContextRegistrationOptions(typeof(NotificationsDbContextApp), context.Services);
        repositoryOptions.AddDefaultRepositories(includeAllEntities: true);
        new EfCoreRepositoryRegistrar(repositoryOptions).AddRepositories();

    }
}
