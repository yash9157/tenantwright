using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace Shared.EntityFrameworkCore.Migrations;

public static class SharedEfCoreAutoMigrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TDbContext"/> for startup migration. A DbContext tenants may move to a dedicated
    /// database also gets the <see cref="TenantDatabaseMigrationReconciler"/> (once per host), which migrates a
    /// dedicated database tenant work finds unmigrated.
    /// </summary>
    public static IServiceCollection AddSharedEfCoreAutoMigration<TDbContext>(this IServiceCollection services)
        where TDbContext : Microsoft.EntityFrameworkCore.DbContext
    {
        services.Configure<SharedEfCoreAutoMigrationOptions>(options =>
        {
            options.DbContextTypes.Add(typeof(TDbContext));
        });

        if (TenantDatabaseMigrator.SupportsDedicatedDatabases(typeof(TDbContext)) &&
            !services.Any(descriptor => descriptor.ServiceType == typeof(TenantDatabaseMigrationReconciler)))
        {
            services.AddSingleton<TenantDatabaseMigrationReconciler>();
            services.AddHostedService(provider => provider.GetRequiredService<TenantDatabaseMigrationReconciler>());
        }

        return services;
    }
}
