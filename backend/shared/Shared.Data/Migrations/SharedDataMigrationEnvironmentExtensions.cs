using System;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Data.Migrations;

public static class SharedDataMigrationEnvironmentExtensions
{
    public static void AddDataMigrationEnvironment(this IServiceCollection services, SharedDataMigrationEnvironment? environment = null)
    {
        services.AddSingleton(environment ?? new SharedDataMigrationEnvironment());
    }

    public static SharedDataMigrationEnvironment? GetDataMigrationEnvironment(this IServiceProvider serviceProvider)
    {
        return serviceProvider.GetService<SharedDataMigrationEnvironment>();
    }

    public static bool IsDataMigrationEnvironment(this IServiceProvider serviceProvider)
    {
        return serviceProvider.GetDataMigrationEnvironment() != null;
    }
}
