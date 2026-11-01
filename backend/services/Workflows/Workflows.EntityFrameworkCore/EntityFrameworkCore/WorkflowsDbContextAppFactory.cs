using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.GlobalFilters;
using Shared.Modularity.DependencyInjection;

namespace Workflows.EntityFrameworkCore.EntityFrameworkCore;

public class WorkflowsDbContextAppFactory : IDesignTimeDbContextFactory<WorkflowsDbContextApp>
{
    public WorkflowsDbContextApp CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("WORKFLOWS_DB_CONNECTION")
                               ?? "Host=localhost;Port=5433;Database=tenantwright_workflows;Username=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<WorkflowsDbContextApp>();
        optionsBuilder.UseNpgsql(connectionString);

        var context = new WorkflowsDbContextApp(optionsBuilder.Options);

        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<SharedDbContextOptions>(_ => { });
        services.Configure<SharedEfCoreGlobalFilterOptions>(_ => { });
        context.ServiceProvider = services.BuildServiceProvider();

        return context;
    }
}
