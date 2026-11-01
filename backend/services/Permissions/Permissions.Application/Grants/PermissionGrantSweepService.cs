using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Permissions.Application.Caching;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Uow.Abstractions;

namespace Permissions.Application.Grants;

/// <summary>
/// Hard-deletes expired <c>PermissionGrant</c> rows (<c>ExpiresAtUtc &lt;= now</c>) across every tenant
/// and bumps the affected providers' stamps so cached snapshots invalidate promptly rather than at the
/// snapshot cache TTL. Runs host-context (no ambient tenant), so the query uses
/// <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/> to see every tenant's rows.
/// </summary>
public class PermissionGrantSweepService
{
    private readonly IDbContextProvider<PermissionsDbContextApp> _dbContextProvider;
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly PermissionStampService _stampService;
    private readonly PermissionProviderGrantCacheSyncService _grantCacheSync;
    private readonly IOptionsMonitor<PermissionGrantSweepOptions> _options;
    private readonly ILogger<PermissionGrantSweepService> _logger;

    public PermissionGrantSweepService(
        IDbContextProvider<PermissionsDbContextApp> dbContextProvider,
        IUnitOfWorkExecutor unitOfWorkExecutor,
        PermissionStampService stampService,
        PermissionProviderGrantCacheSyncService grantCacheSync,
        IOptionsMonitor<PermissionGrantSweepOptions> options,
        ILogger<PermissionGrantSweepService> logger)
    {
        _dbContextProvider = dbContextProvider;
        _unitOfWorkExecutor = unitOfWorkExecutor;
        _stampService = stampService;
        _grantCacheSync = grantCacheSync;
        _options = options;
        _logger = logger;
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return 0;
        }

        var batchSize = Math.Clamp(options.BatchSize, 1, 10_000);
        var utcNow = DateTime.UtcNow;
        var totalRemoved = 0;
        var affectedProviders = new HashSet<(Guid? TenantId, string ProviderName, string ProviderKey)>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var expired = await _unitOfWorkExecutor.ExecuteTransactionalAsync(
                ct => SweepBatchAsync(batchSize, utcNow, ct),
                cancellationToken: cancellationToken);

            if (expired.Count == 0)
            {
                break;
            }

            foreach (var grant in expired)
            {
                affectedProviders.Add((grant.TenantId, grant.ProviderName, grant.ProviderKey));
            }

            totalRemoved += expired.Count;

            if (expired.Count < batchSize)
            {
                break;
            }
        }

        foreach (var (tenantId, providerName, providerKey) in affectedProviders)
        {
            var stamp = await _stampService.GetNextStampAsync(tenantId, providerName, providerKey, cancellationToken: cancellationToken);
            await _grantCacheSync.SyncAsync(tenantId, providerName, providerKey, stamp, considerUow: false, cancellationToken: cancellationToken);
            await _stampService.BumpAsync(tenantId, providerName, providerKey, stamp, considerUow: false, cancellationToken: cancellationToken);
        }

        if (totalRemoved > 0)
        {
            _logger.LogInformation(
                "Permission grant sweep removed {Count} expired grants across {ProviderCount} providers.",
                totalRemoved,
                affectedProviders.Count);
        }

        return totalRemoved;
    }

    private async Task<List<ExpiredGrantProjection>> SweepBatchAsync(
        int batchSize,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var expired = await dbContext.PermissionGrants
            .IgnoreQueryFilters()
            .WhereOwnedByCurrentDatabase()
            .Where(x => x.ExpiresAtUtc != null && x.ExpiresAtUtc <= utcNow)
            .OrderBy(x => x.Id)
            .Take(batchSize)
            .Select(x => new ExpiredGrantProjection(
                x.Id,
                x.TenantId,
                x.ProviderName,
                x.ProviderKey,
                x.Name,
                x.Effect,
                x.ExpiresAtUtc))
            .ToListAsync(cancellationToken);

        foreach (var grant in expired)
        {
            await dbContext.PermissionGrantHistoryEntries.AddAsync(
                new PermissionGrantHistoryEntry(
                    id: Guid.CreateVersion7(),
                    tenantId: grant.TenantId,
                    providerName: grant.ProviderName,
                    providerKey: grant.ProviderKey,
                    permissionName: grant.Name,
                    resourceName: null,
                    resourceKey: null,
                    resourceOwnerRevision: null,
                    changeType: PermissionGrantHistoryChangeType.Expired,
                    oldEffect: grant.Effect,
                    newEffect: null,
                    expiresAtUtc: grant.ExpiresAtUtc,
                    reason: null,
                    ticketId: null,
                    actorUserId: null,
                    occurredAtUtc: utcNow),
                cancellationToken: cancellationToken);
        }

        if (expired.Count == 0)
        {
            return expired;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var ids = expired.Select(x => x.Id).ToArray();
        await dbContext.PermissionGrants
            .IgnoreQueryFilters()
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
        return expired;
    }

    private sealed record ExpiredGrantProjection(
        Guid Id,
        Guid? TenantId,
        string ProviderName,
        string ProviderKey,
        string Name,
        PermissionGrantEffect Effect,
        DateTime? ExpiresAtUtc);
}
