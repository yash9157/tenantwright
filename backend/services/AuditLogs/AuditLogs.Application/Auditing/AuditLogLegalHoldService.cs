using AuditLogs.Domain.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace AuditLogs.Application.Auditing;

/// <summary>
/// Checks whether audit log rows are protected by an active legal hold (sub-flow 5). Consumed by the
/// archive hot-store delete path (<see cref="AuditLogArchiveProcessor"/>) and the export cleanup path
/// (<see cref="AuditLogExportProcessor"/>) so held rows are never purged. Does not affect sealing
/// (sub-flow 1b) - held rows still get sealed normally, since a hold blocks deletion, not the
/// tamper-evidence chain.
/// </summary>
public sealed class AuditLogLegalHoldService : ITransientDependency
{
    private readonly IDbContextProvider<AuditLogsDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDataFilter _dataFilter;

    public AuditLogLegalHoldService(
        IDbContextProvider<AuditLogsDbContextApp> dbContextProvider,
        ICurrentTenant currentTenant,
        IDataFilter dataFilter)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _dataFilter = dataFilter;
    }

    /// <summary>
    /// Returns true if any active (non-released) legal hold exists at all. Cheap short-circuit so the
    /// common case (no holds configured) avoids per-row filtering entirely.
    /// </summary>
    public async Task<bool> HasAnyActiveHoldAsync(CancellationToken cancellationToken = default)
    {
        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            return await dbContext.Set<AuditLogLegalHold>()
                .AsNoTracking()
                .AnyAsync(x => !x.IsReleased, cancellationToken);
        });
    }

    /// <summary>
    /// Filters out ids of rows covered by an active legal hold from a candidate deletion batch. Loads
    /// active holds once per call (bounded, small table) and checks each candidate row in memory -
    /// avoids per-row round trips while keeping the covering logic (wildcards per dimension) in one
    /// place shared with <see cref="AuditLogLegalHold.Covers"/>.
    /// </summary>
    public async Task<IReadOnlyCollection<long>> FilterOutHeldRowsAsync(
        IReadOnlyCollection<long> candidateIds,
        CancellationToken cancellationToken = default)
    {
        if (candidateIds.Count == 0)
        {
            return candidateIds;
        }

        var activeHolds = await GetActiveHoldsAsync(cancellationToken);
        if (activeHolds.Count == 0)
        {
            return candidateIds;
        }

        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            var rows = await dbContext.AuditLogs
                .AsNoTracking()
                .Where(x => candidateIds.Contains(x.Id))
                .Select(x => new { x.Id, x.TenantId, x.ExecutionTime, x.UserId })
                .ToListAsync(cancellationToken);

            var allowedIds = rows
                .Where(row => !activeHolds.Any(hold => hold.Covers(row.TenantId, row.ExecutionTime, row.UserId)))
                .Select(row => row.Id)
                .ToList();

            return allowedIds;
        });
    }

    /// <summary>
    /// True if the given row (tenant/time/user) is covered by an active legal hold.
    /// </summary>
    public async Task<bool> IsHeldAsync(
        Guid? tenantId,
        DateTime executionTimeUtc,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        var activeHolds = await GetActiveHoldsAsync(cancellationToken);
        return activeHolds.Any(hold => hold.Covers(tenantId, executionTimeUtc, userId));
    }

    private async Task<List<AuditLogLegalHold>> GetActiveHoldsAsync(CancellationToken cancellationToken)
    {
        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            return await dbContext.Set<AuditLogLegalHold>()
                .AsNoTracking()
                .Where(x => !x.IsReleased)
                .ToListAsync(cancellationToken);
        });
    }

    // Shared-database-only: holds live in the shared registry and gate the shared-database archive purge, the only
    // caller; dedicated tenant databases are never archived or purged.
    private async Task<T> ExecuteWithHostMultiTenantFilterDisabledAsync<T>(Func<Task<T>> action)
    {
        if (_currentTenant.Id.HasValue)
        {
            return await action();
        }

        using (_dataFilter.Disable<IMultiTenant>())
        {
            return await action();
        }
    }
}
