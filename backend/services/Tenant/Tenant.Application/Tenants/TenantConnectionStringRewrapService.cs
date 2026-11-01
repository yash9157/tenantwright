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

/// <summary>
/// Re-wraps every stored tenant connection string under the current protection
/// key. This is the migration step of key rotation: after a new key is added to
/// <c>TenantConnectionStrings:Protection:Keys</c> and set as
/// <c>CurrentKeyId</c>, every service still needs to move its stored/cached
/// ciphertext off the old key before that key can be safely removed. Retiring
/// the old key before every service has rewrapped and refreshed its cache is a
/// platform-wide decrypt failure — key retirement is a two-deploy operation
/// (see the Tenant database docs for the runbook).
/// </summary>
public sealed class TenantConnectionStringRewrapService : ITransientDependency
{
    private const int BatchSize = 100;
    private const string LockName = "tenant:connection-strings:key-rewrap";
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
    private readonly TenantCacheManager _tenantCacheManager;
    private readonly ILogger<TenantConnectionStringRewrapService> _logger;

    public TenantConnectionStringRewrapService(
        IDbContextProvider<TenantDbContextApp> dbContextProvider, ITenantConnectionStringProtector protector, IOptions<TenantConnectionStringProtectionOptions> options, IDistributedLock distributedLock, TenantCacheManager tenantCacheManager, ILogger<TenantConnectionStringRewrapService> logger)
    {
        _dbContextProvider = dbContextProvider;
        _protector = protector;
        _options = options;
        _distributedLock = distributedLock;
        _tenantCacheManager = tenantCacheManager;
        _logger = logger;
    }

    public async Task<int> RewrapAsync(CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var options = _options.Value;

        // Rewrap is the key-rotation migration step. With no current protection key configured
        // (e.g. local/dev, or an environment not using connection-string protection) there is
        // nothing to rotate onto, so skip rather than crashing host startup. When a key is
        // configured (staging/production) behavior is unchanged.
        if (string.IsNullOrWhiteSpace(options.CurrentKeyId))
        {
            _logger.LogInformation(
                "Skipping tenant connection-string key rewrap: " +
                "TenantConnectionStrings:Protection:CurrentKeyId is not configured.");
            return 0;
        }

        await using var handle = await _distributedLock.AcquireAsync(
            LockName,
            LockOptions,
            cancellationToken);

        var currentKeyId = options.CurrentKeyId;
        var currentKeyVersion = Math.Max(1, options.CurrentKeyVersion);

        var totalRewrapped = 0;
        while (true)
        {
            // Rows already on the current key/version are skipped by this query,
            // which is what makes the rewrap idempotent: a run with nothing left
            // to rotate simply finds zero rows.
            var staleRows = await unitOfWorkDbContext.TenantConnectionStrings
                .Where(x => x.ProtectedValue != null &&
                    (x.ProtectionKeyId != currentKeyId || x.ProtectionKeyVersion != currentKeyVersion))
                .OrderBy(x => x.TenantId)
                .ThenBy(x => x.Name)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (staleRows.Count == 0)
            {
                break;
            }

            foreach (var row in staleRows)
            {
                // Unprotect reads the key id embedded in the stored envelope and
                // throws a clear InvalidOperationException if that key is no
                // longer configured in Keys. Do not swallow that: an old key
                // must stay in Keys until every row referencing it has been
                // rewrapped.
                var plaintext = _protector.Unprotect(row.TenantId, row.Name, row.ProtectedValue!);
                var rewrapped = _protector.Protect(row.TenantId, row.Name, plaintext);
                row.SetProtectedValue(rewrapped, currentKeyId, currentKeyVersion);
            }

            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
            totalRewrapped += staleRows.Count;
        }

        if (totalRewrapped > 0)
        {
            // Mandatory: the protected value changed, so every service's cached
            // ciphertext (Valkey, by-id/by-name/list) is now stale. Without this
            // refresh, other services keep serving old-key ciphertext and break
            // the moment the old key is removed from Keys.
            await _tenantCacheManager.RefreshAllAsync(cancellationToken);

            _logger.LogInformation(
                "Re-wrapped {ConnectionStringCount} tenant connection strings under the current protection key.",
                totalRewrapped);
        }

        return totalRewrapped;
    }
}
