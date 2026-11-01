using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.GlobalFilters;
using Shared.Modularity.DependencyInjection;

namespace Identity.EntityFrameworkCore.EntityFrameworkCore;

public class IdentityDbContextAppFactory : IDesignTimeDbContextFactory<IdentityDbContextApp>
{
    public IdentityDbContextApp CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("IDENTITY_DB_CONNECTION") ??
            "Host=localhost;Port=5433;Database=tenantwright_identity;Username=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<IdentityDbContextApp>();
        optionsBuilder.UseNpgsql(connectionString);

        var context = new IdentityDbContextApp(optionsBuilder.Options);

        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<SharedDbContextOptions>(_ => { });
        services.Configure<SharedEfCoreGlobalFilterOptions>(_ => { });

        context.ServiceProvider = services.BuildServiceProvider();
        return context;
    }
}
