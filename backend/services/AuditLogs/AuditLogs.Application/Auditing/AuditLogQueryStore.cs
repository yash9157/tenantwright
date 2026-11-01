using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;

namespace AuditLogs.Application.Auditing;

public class AuditLogQueryStore : ITransientDependency
{
    private readonly IDbContextProvider<AuditLogsDbContextApp> _dbContextProvider;

    public AuditLogQueryStore(IDbContextProvider<AuditLogsDbContextApp> dbContextProvider)
    {
        _dbContextProvider = dbContextProvider;
    }

    public virtual async Task<AuditLog?> FindDetailsAsync(long id, CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        return await dbContext.Set<AuditLog>()
            .AsNoTracking()
            .IncludeDetails()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public virtual async Task<IQueryable<AuditLog>> GetListQueryableAsync(AuditLogFilter filter, bool includeDetails = false)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        return AuditLogQueryBuilder.Build(dbContext, filter, includeDetails);
    }

    public virtual async Task<IQueryable<EntityChange>> GetEntityChangeQueryableAsync(EntityChangeFilter filter, bool includeDetails = false)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        return dbContext.Set<EntityChange>()
            .AsNoTracking()
            .IncludeDetails(includeDetails)
            .ApplyFilter(filter);
    }

    public virtual async Task<EntityChange?> FindEntityChangeDetailsAsync(
        long id,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        return await dbContext.Set<EntityChange>()
            .AsNoTracking()
            .IncludeDetails()
            .FirstOrDefaultAsync(
                entityChange => entityChange.Id == id && entityChange.TenantId == tenantId,
                cancellationToken);
    }

    public virtual async Task<AuditArchiveManifest?> FindManifestByPartitionKeyAsync(
        string partitionKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partitionKey))
        {
            return null;
        }

        var normalized = partitionKey.Trim();
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        return await dbContext.Set<AuditArchiveManifest>()
            .Where(x => x.PartitionKey == normalized)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public virtual async Task<IReadOnlyDictionary<string, AuditArchiveManifestStatus>> GetStatusMapByPartitionKeysAsync(
        IReadOnlyCollection<string> partitionKeys,
        CancellationToken cancellationToken = default)
    {
        if (partitionKeys.Count == 0)
        {
            return new Dictionary<string, AuditArchiveManifestStatus>(StringComparer.Ordinal);
        }

        var normalized = partitionKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalized.Count == 0)
        {
            return new Dictionary<string, AuditArchiveManifestStatus>(StringComparer.Ordinal);
        }

        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var rows = await dbContext.Set<AuditArchiveManifest>()
            .AsNoTracking()
            .Where(x => normalized.Contains(x.PartitionKey))
            .Select(x => new { x.PartitionKey, x.Status, x.Id })
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.PartitionKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Status,
                StringComparer.Ordinal);
    }

    public virtual Task<IReadOnlyList<AuditArchiveManifest>> GetRestoreQueuedAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        maxCount = Math.Clamp(maxCount, 1, 100);
        return GetManifestListAsync(
            query => query
                .Where(x => x.Status == AuditArchiveManifestStatus.RestoreQueued)
                .OrderBy(x => x.ArchivedAtUtc)
                .ThenBy(x => x.Id)
                .Take(maxCount),
            cancellationToken);
    }

    public virtual Task<IReadOnlyList<AuditArchiveManifest>> GetExpiredArchiveDeleteCandidatesAsync(
        DateTime utcNow,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        maxCount = Math.Clamp(maxCount, 1, 100);
        return GetManifestListAsync(
            query => query
                .Where(x =>
                    x.DeleteAfterUtc <= utcNow &&
                    x.Status != AuditArchiveManifestStatus.Deleted &&
                    (x.Status == AuditArchiveManifestStatus.Archived ||
                     x.Status == AuditArchiveManifestStatus.Restored ||
                     x.Status == AuditArchiveManifestStatus.RestoreFailed))
                .OrderBy(x => x.DeleteAfterUtc)
                .ThenBy(x => x.Id)
                .Take(maxCount),
            cancellationToken);
    }

    private async Task<IReadOnlyList<AuditArchiveManifest>> GetManifestListAsync(
        Func<IQueryable<AuditArchiveManifest>, IQueryable<AuditArchiveManifest>> buildQuery,
        CancellationToken cancellationToken)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        return await buildQuery(dbContext.Set<AuditArchiveManifest>().AsNoTracking())
            .ToListAsync(cancellationToken);
    }
}
