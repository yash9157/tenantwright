using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.Data.ConnectionStrings;
using Shared.Modularity.DependencyInjection;
using Tenant.Domain.Tenants;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
namespace Tenant.Application.Tenants;

public sealed class TenantConnectionStringBackfillService : ITransientDependency
{
    private const int BatchSize = 100;
    private const string LockName = "tenant:connection-strings:protection-backfill";
    private static readonly DistributedLockAcquireOptions LockOptions = new()
    {
        AcquireTimeoutMs = 30000,
        LeaseMs = 300000,
        FailureMode = DistributedLockFailureMode.FailClosed,
        IgnoreTenant = true
    };

    private readonly IDbContextProvider<TenantDbContextApp> _dbContextProvider;
    private readonly ITenantConnectionStringProtector _protector;
    private readonly IOptions<TenantConnectionStringProtectionOptions> _options;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<TenantConnectionStringBackfillService> _logger;

    public TenantConnectionStringBackfillService(
        IDbContextProvider<TenantDbContextApp> dbContextProvider, ITenantConnectionStringProtector protector, IOptions<TenantConnectionStringProtectionOptions> options, IDistributedLock distributedLock, ILogger<TenantConnectionStringBackfillService> logger)
    {
        _dbContextProvider = dbContextProvider;
        _protector = protector;
        _options = options;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    public async Task<int> BackfillAsync(CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        await using var handle = await _distributedLock.AcquireAsync(
                    LockName,
                    LockOptions,
                    cancellationToken);

        var totalUpdated = 0;
        while (true)
        {
            var legacyRows = await unitOfWorkDbContext.TenantConnectionStrings
                .Where(x => x.Value != null && x.ProtectedValue == null)
                .OrderBy(x => x.TenantId)
                .ThenBy(x => x.Name)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (legacyRows.Count == 0)
            {
                break;
            }

            var options = _options.Value;
            var keyId = options.CurrentKeyId
                ?? throw new InvalidOperationException(
                    "TenantConnectionStrings:Protection:CurrentKeyId is required for plaintext backfill.");
            foreach (var row in legacyRows)
            {
                var protectedValue = _protector.Protect(row.TenantId, row.Name, row.Value!);
                row.SetProtectedValue(
                    protectedValue,
                    keyId,
                    options.CurrentKeyVersion);
            }

            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
            totalUpdated += legacyRows.Count;
        }

        if (await unitOfWorkDbContext.TenantConnectionStrings.AnyAsync(x => x.Value != null, cancellationToken))
        {
            throw new InvalidOperationException(
                "Tenant connection-string protection backfill completed with plaintext rows remaining.");
        }

        if (totalUpdated > 0)
        {
            _logger.LogInformation(
                "Protected {ConnectionStringCount} legacy tenant connection strings.",
                totalUpdated);
        }

        return totalUpdated;
    }
}
