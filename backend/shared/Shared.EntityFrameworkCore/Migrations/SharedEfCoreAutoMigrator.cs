using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;
using Shared.Modularity.DependencyInjection;

namespace Shared.EntityFrameworkCore.Migrations;

public sealed class SharedEfCoreAutoMigrator : ITransientDependency
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<SharedEfCoreAutoMigrationOptions> _options;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<SharedEfCoreAutoMigrator> _logger;

    public SharedEfCoreAutoMigrator(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<SharedEfCoreAutoMigrationOptions> options,
        IDistributedLock distributedLock,
        ILogger<SharedEfCoreAutoMigrator>? logger = null)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _distributedLock = distributedLock;
        _logger = logger ?? NullLogger<SharedEfCoreAutoMigrator>.Instance;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var dbContextTypes = _options.Value.DbContextTypes
            .Distinct()
            .ToList();

        if (dbContextTypes.Count == 0)
        {
            return;
        }

        using var scope = _serviceScopeFactory.CreateScope();

        foreach (var dbContextType in dbContextTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!typeof(DbContext).IsAssignableFrom(dbContextType))
            {
                continue;
            }

            var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(dbContextType);
            if (!dbContext.Database.IsRelational())
            {
                continue;
            }

            var lockName = EfCoreDistributedLockNameFactory.BuildMigrationLockName(dbContextType, dbContext);
            await using var migrationLock = await _distributedLock.AcquireAsync(
                lockName,
                new DistributedLockAcquireOptions
                {
                    PolicyName = DistributedLockPolicyNames.EfCoreMigration,
                    IgnoreTenant = true
                },
                cancellationToken);

            _logger.LogInformation("Applying EF Core migrations for {DbContext}", dbContextType.FullName);
            await dbContext.Database.MigrateAsync(cancellationToken);
        }
    }
}
