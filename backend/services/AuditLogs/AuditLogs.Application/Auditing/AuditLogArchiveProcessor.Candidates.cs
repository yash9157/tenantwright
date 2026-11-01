using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using AuditLogs.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.BlobStoring;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;

namespace AuditLogs.Application.Auditing;

public sealed partial class AuditLogArchiveProcessor
{
    private async Task<int> ProcessArchiveCandidatesAsync(AuditLogArchiveOptions options, CancellationToken cancellationToken)
    {
        var candidates = await GetArchiveCandidatesAsync(options, cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var processed = 0;
        foreach (var candidate in candidates)
        {
            if (processed >= options.MaxPartitionsPerCycle)
            {
                break;
            }

            if (await ProcessArchivePartitionAsync(candidate, options, cancellationToken))
            {
                processed++;
            }
        }

        return processed;
    }

    private async Task<IReadOnlyList<AuditArchivePartition>> GetArchiveCandidatesAsync(
        AuditLogArchiveOptions options,
        CancellationToken cancellationToken)
    {
        var cutoffUtc = DateTime.UtcNow.AddDays(-options.HotRetentionDays);
        var raw = await _uowExecutor.ExecuteNonTransactionalAsync(async ct =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            return await dbContext.AuditLogs
                .AsNoTracking()
                .Where(x => x.ExecutionTime < cutoffUtc)
                .GroupBy(x => new { x.TenantId, Year = x.ExecutionTime.Year, Month = x.ExecutionTime.Month })
                .Select(x => new ArchivePartitionGroup
                {
                    TenantId = x.Key.TenantId,
                    Year = x.Key.Year,
                    Month = x.Key.Month
                })
                .OrderBy(x => x.Year)
                .ThenBy(x => x.Month)
                .ThenBy(x => x.TenantId)
                .Take(options.MaxPartitionsPerCycle * 10)
                .ToListAsync(ct);
        }, cancellationToken);

        var candidates = new List<AuditArchivePartition>(raw.Count);
        foreach (var group in raw)
        {
            if (group.Month is < 1 or > 12)
            {
                continue;
            }

            candidates.Add(new AuditArchivePartition(group.TenantId, group.Year, group.Month));
        }

        if (candidates.Count == 0)
        {
            return candidates;
        }

        var partitionKeys = candidates.Select(x => x.PartitionKey).ToList();
        var statusMap = await _uowExecutor.ExecuteNonTransactionalAsync(async ct =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            var rows = await dbContext.Set<AuditArchiveManifest>()
                .AsNoTracking()
                .Where(x => partitionKeys.Contains(x.PartitionKey))
                .Select(x => new { x.PartitionKey, x.Status, x.Id })
                .OrderBy(x => x.Id)
                .ToListAsync(ct);

            return (IReadOnlyDictionary<string, AuditArchiveManifestStatus>)rows
                .GroupBy(x => x.PartitionKey, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().Status,
                    StringComparer.Ordinal);
        }, cancellationToken);

        var result = new List<AuditArchivePartition>(candidates.Count);
        foreach (var partition in candidates)
        {
            if (!statusMap.TryGetValue(partition.PartitionKey, out var status) ||
                status is AuditArchiveManifestStatus.PendingArchive or AuditArchiveManifestStatus.ArchiveFailed)
            {
                result.Add(partition);
            }
        }

        return result;
    }

}
