using AuditLogs.Domain;
using AuditLogs.Domain.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.DependencyInjection;
using Shared.EntityFrameworkCore.ExceptionTranslation;
using Shared.EntityFrameworkCore.Migrations;
using Shared.EntityFrameworkCore.Messaging;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;

namespace AuditLogs.EntityFrameworkCore;

[DependsOn(typeof(AuditLogsDomainModule))]
[DependsOn(typeof(SharedEntityFrameworkCoreModule))]
public class AuditLogsEntityFrameworkCoreModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Configuration;

        context.Services.Configure<SharedDatabaseExceptionTranslationOptions>(options =>
        {
            options.Configure<AuditLogsDbContextApp>(
                "IX_AuditLogs_EventId",
                new SharedDatabaseConstraintDescriptor(
                    "audit_event_already_recorded",
                    "The audit event was already recorded."));
        });

        context.Services.AddDbContext<AuditLogsDbContextApp>(options =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"));
            options.AddSharedDbContextOptionsExtension();
        });

        context.Services.AddSharedEfCoreAutoMigration<AuditLogsDbContextApp>();

        var repositoryOptions = new SharedDbContextRegistrationOptions(typeof(AuditLogsDbContextApp), context.Services);
        repositoryOptions.AddDefaultRepositories(includeAllEntities: true);
        new EfCoreRepositoryRegistrar(repositoryOptions).AddRepositories();

        context.Services.AddTenantWrightProtectedReplayIdempotencyStore<AuditLogsDbContextApp>();
    }
}
