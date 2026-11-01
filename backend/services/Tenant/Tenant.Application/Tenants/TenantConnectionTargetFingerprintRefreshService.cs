using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Data.ConnectionStrings;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;

namespace Tenant.Application.Tenants;

/// <summary>
/// Recomputes every stored <c>TargetFingerprint</c> from the decrypted connection string with the current
/// <see cref="PostgreSqlDatabaseTarget"/> normalization. Runs at Tenant startup under a global lock, after protection
/// backfill and key rewrap; it is idempotent (a row whose fingerprint is current is not written) and also fills
/// fingerprints missing from rows written before they existed.
/// <para>
/// Fingerprints are derived values, so a normalization change is migrated by recomputing them rather than by a schema
/// migration. Two assignments that only now resolve to the same target (an alias the earlier normalization missed)
/// cannot both hold that fingerprint under the unique index: both are cleared instead and reported as an error, so
/// every new assignment is compared against them on the decrypted target and an operator can correct them. The work
/// is bounded by the number of dedicated assignments (one row per tenant and dedicated name), never by tenant count.
/// </para>
/// </summary>
public sealed class TenantConnectionTargetFingerprintRefreshService : ITransientDependency
{
    private const string LockName = "tenant:connection-strings:target-fingerprints";
    private static readonly DistributedLockAcquireOptions LockOptions = new()
    {
        AcquireTimeoutMs = 30000,
        LeaseMs = 300000,
        FailureMode = DistributedLockFailureMode.FailClosed,
        IgnoreTenant = true
    };

    private readonly IDbContextProvider<TenantDbContextApp> _dbContextProvider;
    private readonly ITenantConnectionStringProtector _protector;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<TenantConnectionTargetFingerprintRefreshService> _logger;

    public TenantConnectionTargetFingerprintRefreshService(
        IDbContextProvider<TenantDbContextApp> dbContextProvider,
        ITenantConnectionStringProtector protector,
        IDistributedLock distributedLock,
        ILogger<TenantConnectionTargetFingerprintRefreshService> logger)
    {
        _dbContextProvider = dbContextProvider;
        _protector = protector;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    /// <summary>Returns how many rows changed (fingerprints rewritten plus fingerprints cleared).</summary>
    public async Task<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        await using var handle = await _distributedLock.AcquireAsync(LockName, LockOptions, cancellationToken);

        var rows = await dbContext.TenantConnectionStrings
            .Where(row => row.ProtectedValue != null || row.Value != null)
            .OrderBy(row => row.TenantId)
            .ThenBy(row => row.Name)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return 0;
        }

        var current = rows
            .Select(row => (Row: row, Fingerprint: PostgreSqlDatabaseTarget.FingerprintOf(
                _protector.Unprotect(row.TenantId, row.Name, row.GetStoredValue()!))))
            .ToList();

        var rewritten = 0;
        var cleared = 0;
        foreach (var group in current.GroupBy(entry => entry.Fingerprint, StringComparer.Ordinal))
        {
            var assignments = group.ToList();
            if (assignments.Count == 1)
            {
                var (row, fingerprint) = assignments[0];
                if (!string.Equals(row.TargetFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    row.SetTargetFingerprint(fingerprint);
                    rewritten++;
                }

                continue;
            }

            foreach (var (row, _) in assignments.Where(entry => entry.Row.TargetFingerprint != null))
            {
                row.ClearTargetFingerprint();
                cleared++;
            }

            _logger.LogError(
                "{AssignmentCount} dedicated database assignments resolve to one database target ({Names}); their target fingerprints were cleared so new assignments are compared on the decrypted target. Re-point all but one of them. TenantHashes={TenantHashes}, TargetKey={TargetKey}.",
                assignments.Count,
                string.Join(",", assignments.Select(entry => entry.Row.Name).Distinct(StringComparer.Ordinal)),
                string.Join(",", assignments.Select(entry => TenantResolutionHash.HashTenantId(entry.Row.TenantId))),
                group.Key[..12]);
        }

        if (rewritten + cleared == 0)
        {
            return 0;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Refreshed tenant connection target fingerprints: {RewrittenCount} rewritten, {ClearedCount} cleared as duplicates.",
            rewritten,
            cleared);
        return rewritten + cleared;
    }
}
