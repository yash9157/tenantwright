using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shared.CurrentUser;
using Shared.Data.ConnectionStrings;
using Shared.Domain;
using Shared.EntityFrameworkCore.ConnectionStrings;
using Shared.EntityFrameworkCore.Repositories;
using Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Migrations;
using Shared.EntityFrameworkCore.ExceptionTranslation;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.Modules;
using Shared.Modularity.Context;
using Shared.MultiTenancy;
using Shared.DistributedLocking;
using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace Shared.EntityFrameworkCore;

[DependsOn(typeof(SharedDomainModule))]
[DependsOn(typeof(CurrentUserModule))]
[DependsOn(typeof(MultiTenancyModule))]
[DependsOn(typeof(DistributedLockingModule))]
public class SharedEntityFrameworkCoreModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.TryAddTransient(typeof(IDbContextProvider<>), typeof(UnitOfWorkDbContextProvider<>));
        context.Services.AddOptions<SharedDatabaseExceptionTranslationOptions>();
        context.Services.Replace(ServiceDescriptor.Transient<IConnectionStringResolver, TenantConnectionStringResolver>());
        context.Services.Configure<TenantConnectionStringProtectionOptions>(
            context.Configuration.GetSection(TenantConnectionStringProtectionOptions.SectionName));

        context.Services.Configure<SharedDbContextOptions>(options =>
        {
            options.PreConfigure(sharedDbContextConfigurationContext =>
            {
                sharedDbContextConfigurationContext.DbContextOptions
                    .ConfigureWarnings(warnings =>
                    {
                        warnings.Ignore(CoreEventId.LazyLoadOnDisposedContextWarning);
                    });
            });
        });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
        var resolverOptions = context.ServiceProvider
            .GetRequiredService<IOptions<SharedConnectionStringResolverOptions>>()
            .Value;

        if (!isDevelopment && !resolverOptions.ThrowOnMissing)
        {
            throw new InvalidOperationException(
                $"{SharedConnectionStringResolverOptions.SectionName}:ThrowOnMissing must be true outside Development.");
        }

    }

    public override async Task OnApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();
        var autoMigrate = string.Equals(
            configuration["EfCore:AutoMigrate"],
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (!autoMigrate)
        {
            return;
        }

        using var scope = context.ServiceProvider.CreateScope();
        var migrator = scope.ServiceProvider.GetRequiredService<SharedEfCoreAutoMigrator>();
        await migrator.MigrateAsync();

        var migrateAndExit = string.Equals(
            configuration["EfCore:MigrateAndExit"],
            "true",
            StringComparison.OrdinalIgnoreCase);

        // Dedicated tenant databases follow the shared one. A long-running host only warns about databases it could
        // not migrate: their tenants fail closed through TenantDatabaseMigrationGuard, which hands them to the
        // TenantDatabaseMigrationReconciler; the next TenantDatabaseChangedEto or restart retries them too. The
        // one-shot migration job must not report success when a known
        // dedicated database failed. An unavailable tenant list is only warned about (by the topology), because a
        // fresh environment has no tenant cache before the Tenant service first starts.
        var autoMigration = scope.ServiceProvider.GetRequiredService<IOptions<SharedEfCoreAutoMigrationOptions>>().Value;
        if (autoMigration.DbContextTypes.Any(TenantDatabaseMigrator.SupportsDedicatedDatabases))
        {
            var tenantReport = await scope.ServiceProvider
                .GetRequiredService<TenantDatabaseMigrator>()
                .MigrateAllDedicatedAsync();
            if (migrateAndExit && tenantReport.Failed > 0)
            {
                throw new InvalidOperationException(
                    $"Dedicated tenant database migration failed for {tenantReport.Failed} database(s).");
            }
        }

        // Migrate-and-exit lets a one-shot migration Job run schema changes with the
        // schema-owner credential and then terminate, so the long-running pods keep
        // DML-only rights and never need DDL. Without it the same image would migrate
        // and then serve HTTP forever, and the Job would never reach Completed.
        //
        // It also removes a concurrency hazard: with AutoMigrate alone, every replica
        // races to migrate on startup. The Job runs exactly once, before any pod.
        if (!migrateAndExit)
        {
            return;
        }

        // Defer shutdown until every module has completed its initialization work.
        // Scheduler providers use that phase to prepare their own schemas and register
        // recurring definitions with the schema-owner credential. The modular host
        // consumes this signal before post-initialization or HTTP/worker startup.
        context.ServiceProvider
            .GetRequiredService<ApplicationInitializationExitSignal>()
            .RequestExit();
    }
}
