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
using Shared;
using Shared.Domain;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;

namespace AuditLogs.Application.Auditing;

public sealed partial class AuditLogArchiveProcessor
{
    private async Task CleanupExpiredArchivedBlobsAsync(
        AuditLogArchiveOptions options,
        CancellationToken cancellationToken)
    {
        var utcNow = DateTime.UtcNow;
        var maxCount = Math.Clamp(options.MaxPartitionsPerCycle, 1, 100);
        var expired = await _uowExecutor.ExecuteNonTransactionalAsync(async ct =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            return await dbContext.Set<AuditArchiveManifest>()
                .AsNoTracking()
                .Where(x =>
                    x.DeleteAfterUtc <= utcNow &&
                    x.Status != AuditArchiveManifestStatus.Deleted &&
                    (x.Status == AuditArchiveManifestStatus.Archived ||
                     x.Status == AuditArchiveManifestStatus.Restored ||
                     x.Status == AuditArchiveManifestStatus.RestoreFailed))
                .OrderBy(x => x.DeleteAfterUtc)
                .ThenBy(x => x.Id)
                .Take(maxCount)
                .ToListAsync(ct);
        }, cancellationToken);

        foreach (var manifest in expired)
        {
            if (manifest.Status == AuditArchiveManifestStatus.Deleted)
            {
                continue;
            }

            try
            {
                var (_, blobName) = ParseArchiveUri(manifest.ArchiveUri, options);
                await ExecuteWithRetryAsync(
                    _ => _archiveBlobContainer.DeleteAsync(blobName, cancellationToken),
                    ResolveBlobRetryAttempts(options),
                    cancellationToken);

                // Post-blob-delete manifest state runs in a fresh transactional scope after the blob is
                // removed above (outside any database transaction).
                var manifestId = manifest.Id;
                await _uowExecutor.ExecuteTransactionalAsync(async ct =>
                {
                    var tracked = await _manifestRepository.GetAsync(manifestId, includeDetails: false, cancellationToken: ct);
                    tracked.MarkDeleted();
                    await _manifestRepository.UpdateAsync(tracked, autoSave: false, cancellationToken: ct);
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FailureCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "cleanup_failed"),
                    new KeyValuePair<string, object?>("stage", "cleanup"));
                _logger.LogWarning(
                    "Failed to cleanup expired archive blob for manifest {ManifestId}. Reason={ReasonCode}",
                    manifest.Id,
                    ClassifyFailure(ex));
            }
        }
    }

    private async Task<bool> ProcessRestoreManifestAsync(
        long manifestId,
        AuditLogArchiveOptions options,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            // Phase - claim: a fresh transactional scope moves the manifest to Restoring and marks the
            // active restore job Running. Only an immutable snapshot of the values the download/verify
            // phases need leaves the scope; the tracked entity does not.
            var claim = await _uowExecutor.ExecuteTransactionalAsync<(string ArchiveUri, string Checksum)?>(async ct =>
            {
                var manifest = await _manifestRepository.GetAsync(manifestId, includeDetails: false, cancellationToken: ct);
                if (manifest.Status != AuditArchiveManifestStatus.RestoreQueued)
                {
                    return null;
                }

                manifest.MarkRestoring();
                await _manifestRepository.UpdateAsync(manifest, autoSave: false, cancellationToken: ct);
                var restoreJob = await FindActiveRestoreJobAsync(
                    manifestId,
                    AuditArchiveRestoreJobStatus.Queued,
                    ct);
                if (restoreJob != null)
                {
                    restoreJob.MarkRunning(DateTime.UtcNow);
                    await _restoreJobRepository.UpdateAsync(
                        restoreJob,
                        autoSave: false,
                        cancellationToken: ct);
                }

                return (manifest.ArchiveUri, manifest.Checksum);
            }, cancellationToken);

            if (claim == null)
            {
                return false;
            }

            var (archiveUri, expectedChecksum) = claim.Value;

            RestoreRequestsCounter.Add(1);

            var (_, blobName) = ParseArchiveUri(archiveUri, options);
            var tempFilePath = Path.Combine(
                Path.GetTempPath(),
                $"auditlogs-restore-{manifestId}-{Guid.NewGuid():N}.jsonl.gz");

            try
            {
                await using (var blobStream = await GetBlobWithRetryAsync(blobName, ResolveBlobRetryAttempts(options), cancellationToken))
                await using (var output = File.Create(tempFilePath))
                {
                    await blobStream.CopyToAsync(output, cancellationToken);
                }

                var checksum = await ComputeFileChecksumHexAsync(tempFilePath, cancellationToken);
                if (!FixedTimeEqualsHex(checksum, expectedChecksum))
                {
                    throw new InvalidOperationException("Archive checksum mismatch.");
                }

                long restoredRows = 0;
                var batch = new List<ArchivedAuditLogRecord>(Math.Max(100, options.RowBatchSize));

                await using (var archiveStream = File.OpenRead(tempFilePath))
                await using (var gzip = new GZipStream(archiveStream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip, Encoding.UTF8))
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var line = await reader.ReadLineAsync(cancellationToken);
                        if (line == null)
                        {
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        var record = JsonSerializer.Deserialize<ArchivedAuditLogRecord>(line, JsonOptions);
                        if (record == null)
                        {
                            continue;
                        }

                        batch.Add(record);
                        if (batch.Count >= options.RowBatchSize)
                        {
                            restoredRows += await RestoreBatchAsync(batch, cancellationToken);
                            batch.Clear();
                        }

                        if (restoredRows > options.MaxRestoreRowsPerRun)
                        {
                            throw new InvalidOperationException(
                                $"Restore row count exceeds configured limit ({options.MaxRestoreRowsPerRun}).");
                        }
                    }
                }

                if (batch.Count > 0)
                {
                    restoredRows += await RestoreBatchAsync(batch, cancellationToken);
                }

                // Phase - completion: a separate fresh transactional scope records the restored manifest
                // and job after the blob download, checksum verification, and row insertion above.
                await _uowExecutor.ExecuteTransactionalAsync(async ct =>
                {
                    var tracked = await _manifestRepository.GetAsync(manifestId, includeDetails: false, cancellationToken: ct);
                    tracked.MarkRestored(restoredRows);
                    await _manifestRepository.UpdateAsync(tracked, autoSave: false, cancellationToken: ct);
                    var restoreJob = await FindActiveRestoreJobAsync(
                        manifestId,
                        AuditArchiveRestoreJobStatus.Running,
                        ct);
                    if (restoreJob != null)
                    {
                        restoreJob.MarkCompleted(DateTime.UtcNow, restoredRows);
                        await _restoreJobRepository.UpdateAsync(
                            restoreJob,
                            autoSave: false,
                            cancellationToken: ct);
                    }
                }, cancellationToken);

                RowsRestoredCounter.Add(restoredRows, new KeyValuePair<string, object?>("manifest_id", manifestId));
                RestoreDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return true;
            }
            finally
            {
                TryDeleteFile(tempFilePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailureCounter.Add(
                1,
                new KeyValuePair<string, object?>("reason", "restore_failed"),
                new KeyValuePair<string, object?>("stage", "restore"));
            _logger.LogWarning(
                "Restore failed for archive manifest {ManifestId}. Reason={ReasonCode}",
                manifestId,
                ClassifyFailure(ex));

            // Failure state is mandatory. If this transaction cannot commit, propagate that failure so
            // the background job cannot report success while leaving an unresolved claim.
            await _uowExecutor.ExecuteTransactionalAsync(async ct =>
            {
                var tracked = await _manifestRepository.GetAsync(manifestId, includeDetails: false, cancellationToken: ct);
                IncrementFailureCount(tracked, "RestoreFailureCount");
                tracked.MarkRestoreFailed(NormalizeFailureReason(ex));
                await _manifestRepository.UpdateAsync(tracked, autoSave: false, cancellationToken: ct);
                var restoreJob = await FindLatestActiveRestoreJobAsync(manifestId, ct);
                if (restoreJob != null)
                {
                    restoreJob.MarkFailed(DateTime.UtcNow, NormalizeFailureReason(ex));
                    await _restoreJobRepository.UpdateAsync(
                        restoreJob,
                        autoSave: false,
                        cancellationToken: ct);
                }
            }, cancellationToken);

            return false;
        }
    }

    private async Task<AuditArchiveRestoreJob?> FindActiveRestoreJobAsync(
        long manifestId,
        AuditArchiveRestoreJobStatus status,
        CancellationToken cancellationToken)
    {
        var query = await _restoreJobRepository.GetQueryableAsync();
        return await query
            .Where(x => x.ManifestId == manifestId && x.Status == status)
            .OrderByDescending(x => x.RequestedAtUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<AuditArchiveRestoreJob?> FindLatestActiveRestoreJobAsync(
        long manifestId,
        CancellationToken cancellationToken)
    {
        var query = await _restoreJobRepository.GetQueryableAsync();
        return await query
            .Where(x =>
                x.ManifestId == manifestId &&
                (x.Status == AuditArchiveRestoreJobStatus.Queued ||
                 x.Status == AuditArchiveRestoreJobStatus.Running))
            .OrderByDescending(x => x.RequestedAtUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    internal async Task<long> RestoreBatchAsync(IReadOnlyList<ArchivedAuditLogRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return 0;
        }

        // Restore insertion writes rows and calls SaveChanges, so it runs in a fresh transactional scope
        // rather than the non-transactional read helper the code previously (incorrectly) used. EF
        // savepoints keep the per-row unique-conflict fallback usable within the transaction.
        return await _uowExecutor.ExecuteTransactionalAsync(async ct =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            var eventIds = records.Select(x => x.EventId).Distinct().ToList();
            var existingEventIds = await dbContext.AuditLogs
                .AsNoTracking()
                .Where(x => eventIds.Contains(x.EventId))
                .Select(x => x.EventId)
                .ToListAsync(ct);
            var existingSet = existingEventIds.ToHashSet();

            var newEntities = records
                .Where(record => !existingSet.Contains(record.EventId))
                .Select(ToEntity)
                .ToList();
            if (newEntities.Count == 0)
            {
                return 0L;
            }

            try
            {
                await dbContext.AddAuditLogsAsync(newEntities, ct);
                await dbContext.SaveChangesAsync(ct);
                return (long)newEntities.Count;
            }
            catch (SharedConflictException ex) when (IsAuditEventAlreadyRecorded(ex))
            {
                dbContext.ClearChangeTracker();
                var inserted = 0L;
                foreach (var entity in newEntities)
                {
                    try
                    {
                        dbContext.AddAuditLog(entity);
                        await dbContext.SaveChangesAsync(ct);
                        inserted++;
                    }
                    catch (SharedConflictException conflictEx) when (IsAuditEventAlreadyRecorded(conflictEx))
                    {
                        dbContext.ClearChangeTracker();
                    }
                }

                return inserted;
            }
        }, cancellationToken);
    }

}
