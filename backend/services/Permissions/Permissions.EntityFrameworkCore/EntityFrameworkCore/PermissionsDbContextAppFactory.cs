using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.GlobalFilters;
using Shared.Modularity.DependencyInjection;

namespace Permissions.EntityFrameworkCore.EntityFrameworkCore;

public class PermissionsDbContextAppFactory : IDesignTimeDbContextFactory<PermissionsDbContextApp>
{
    public PermissionsDbContextApp CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("PERMISSIONS_DB_CONNECTION") ??
            "Host=localhost;Port=5433;Database=tenantwright_permissions;Username=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<PermissionsDbContextApp>();
        optionsBuilder.UseNpgsql(connectionString);

        var context = new PermissionsDbContextApp(optionsBuilder.Options);

        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<SharedDbContextOptions>(_ => { });
        services.Configure<SharedEfCoreGlobalFilterOptions>(_ => { });

        context.ServiceProvider = services.BuildServiceProvider();
        return context;
    }
}
