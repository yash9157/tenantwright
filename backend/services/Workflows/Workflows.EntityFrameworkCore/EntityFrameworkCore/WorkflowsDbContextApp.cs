using Microsoft.EntityFrameworkCore;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Messaging;
namespace Workflows.EntityFrameworkCore.EntityFrameworkCore;

[ConnectionStringName(TenantWrightDatabaseCatalog.Workflows)]
public class WorkflowsDbContextApp : SharedDbContext<WorkflowsDbContextApp>
{
    public WorkflowsDbContextApp(DbContextOptions<WorkflowsDbContextApp> options) : base(options)
    {
    }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddTenantWrightMessagingEntities();
    }
}
