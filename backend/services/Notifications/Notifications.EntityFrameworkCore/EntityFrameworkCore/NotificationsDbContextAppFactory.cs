using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.GlobalFilters;
using Shared.Modularity.DependencyInjection;

namespace Notifications.EntityFrameworkCore.EntityFrameworkCore;

public class NotificationsDbContextAppFactory : IDesignTimeDbContextFactory<NotificationsDbContextApp>
{
    public NotificationsDbContextApp CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("NOTIFICATIONS_DB_CONNECTION") ??
            "Host=localhost;Port=5433;Database=tenantwright_notifications;Username=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<NotificationsDbContextApp>();
        optionsBuilder.UseNpgsql(connectionString);

        var context = new NotificationsDbContextApp(optionsBuilder.Options);

        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<SharedDbContextOptions>(_ => { });
        services.Configure<SharedEfCoreGlobalFilterOptions>(_ => { });

        context.ServiceProvider = services.BuildServiceProvider();
        return context;
    }
}
