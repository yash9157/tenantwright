using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.GlobalFilters;
using Shared.Modularity.DependencyInjection;

namespace AuditLogs.EntityFrameworkCore.EntityFrameworkCore;

public class AuditLogsDbContextAppFactory : IDesignTimeDbContextFactory<AuditLogsDbContextApp>
{
    public AuditLogsDbContextApp CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("AUDITLOGS_DB_CONNECTION") ??
            "Host=localhost;Port=5433;Database=tenantwright_auditlogs;Username=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<AuditLogsDbContextApp>();
        optionsBuilder.UseNpgsql(connectionString);

        var context = new AuditLogsDbContextApp(optionsBuilder.Options);

        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<SharedDbContextOptions>(_ => { });
        services.Configure<SharedEfCoreGlobalFilterOptions>(_ => { });

        context.ServiceProvider = services.BuildServiceProvider();
        return context;
    }
}
