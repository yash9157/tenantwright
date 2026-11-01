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
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var options = _archiveOptions.CurrentValue;
        if (!options.Enabled)
        {
            return 0;
        }

        if (_currentTenant.Id.HasValue)
        {
            _logger.LogWarning(
                "Skipping archive processor run because tenant context {TenantId} is active. Archive operations are host-only.",
                _currentTenant.Id.Value);
            return 0;
        }

        await using var ownerLock = await _distributedLock.TryAcquireAsync(
            MaintenanceLockName,
            MaintenanceLockOptions,
            cancellationToken);
        if (ownerLock == null)
        {
            _logger.LogDebug("Skipping audit archive maintenance because another owner holds the maintenance lock.");
            return 0;
        }

        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            await RecoverInterruptedClaimsAsync(cancellationToken);
            var processed = 0;
            processed += await ProcessRestoreQueueAsync(options, cancellationToken);
            processed += await ProcessArchiveCandidatesAsync(options, cancellationToken);
            await CleanupExpiredArchivedBlobsAsync(options, cancellationToken);
            return processed;
        });
    }

    private async Task<int> ProcessRestoreQueueAsync(AuditLogArchiveOptions options, CancellationToken cancellationToken)
    {
        if (!options.AllowRestore)
        {
            return 0;
        }

        var queued = await _uowExecutor.ExecuteNonTransactionalAsync(async ct =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            return await dbContext.Set<AuditArchiveManifest>()
                .AsNoTracking()
                .Where(x => x.Status == AuditArchiveManifestStatus.RestoreQueued)
                .OrderBy(x => x.ArchivedAtUtc)
                .ThenBy(x => x.Id)
                .Take(Math.Clamp(options.MaxConcurrentRestorePartitions, 1, 100))
                .Select(x => new { x.Id })
                .ToListAsync(ct);
        }, cancellationToken);
        if (queued.Count == 0)
        {
            return 0;
        }

        var processed = 0;
        foreach (var manifest in queued)
        {
            if (await ProcessRestoreManifestAsync(manifest.Id, options, cancellationToken))
            {
                processed++;
            }
        }

        return processed;
    }

}
