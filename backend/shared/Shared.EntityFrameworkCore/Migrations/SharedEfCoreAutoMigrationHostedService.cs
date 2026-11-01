using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shared.EntityFrameworkCore.Migrations;

public class SharedEfCoreAutoMigrationHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SharedEfCoreAutoMigrationHostedService> _logger;

    public SharedEfCoreAutoMigrationHostedService(
        IServiceProvider serviceProvider,
        ILogger<SharedEfCoreAutoMigrationHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var migrator = scope.ServiceProvider.GetRequiredService<SharedEfCoreAutoMigrator>();

        _logger.LogInformation("Starting EF Core auto-migration.");
        await migrator.MigrateAsync(cancellationToken);
        _logger.LogInformation("Completed EF Core auto-migration.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
