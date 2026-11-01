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
    private async Task<bool> ProcessArchivePartitionAsync(
        AuditArchivePartition partition,
        AuditLogArchiveOptions options,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var manifestId = 0L;
        var blobName = BuildBlobName(partition);
        var archiveUri = BuildArchiveUri(options, blobName);
        var deleteAfterUtc = partition.EndUtc.AddYears(options.BlobRetentionYears);

        try
        {
            // Phase - claim: a fresh transactional scope creates or re-claims the partition manifest and
            // marks it Archiving. Inserts use autoSave so the database-generated manifest id is available
            // for the later generation/completion phases; the transaction still commits on completion.
            var claimedManifestId = await _uowExecutor.ExecuteTransactionalAsync<long?>(async ct =>
            {
                var dbContext = await _dbContextProvider.GetDbContextAsync();
                var manifest = await dbContext.Set<AuditArchiveManifest>()
                    .Where(x => x.PartitionKey == partition.PartitionKey)
                    .OrderBy(x => x.Id)
                    .FirstOrDefaultAsync(ct);
                if (manifest == null)
                {
                    manifest = new AuditArchiveManifest(
                        id: 0,
                        partitionKey: partition.PartitionKey,
                        archivedAtUtc: DateTime.UtcNow,
                        deleteAfterUtc: deleteAfterUtc);
                    manifest.MarkArchiving();
                    await _manifestRepository.InsertAsync(manifest, autoSave: true, cancellationToken: ct);
                    return manifest.Id;
                }

                if (manifest.Status is not (AuditArchiveManifestStatus.PendingArchive or AuditArchiveManifestStatus.ArchiveFailed))
                {
                    return null;
                }

                if (manifest.Status == AuditArchiveManifestStatus.ArchiveFailed &&
                    IsPoisonFailure(manifest, "ArchiveFailureCount"))
                {
                    FailureCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("reason", "archive_poisoned"),
                        new KeyValuePair<string, object?>("stage", "archive_claim"));
                    _logger.LogWarning(
                        "Skipping archive partition {PartitionKey} because it exceeded max failure attempts.",
                        partition.PartitionKey);
                    return null;
                }

                manifest.MarkArchiving();
                await _manifestRepository.UpdateAsync(manifest, autoSave: false, cancellationToken: ct);
                return manifest.Id;
            }, cancellationToken);

            if (claimedManifestId == null)
            {
                return false;
            }

            manifestId = claimedManifestId.Value;

            var generation = await GenerateArchiveFileAsync(partition, options, cancellationToken);
            try
            {
                if (generation.RowCount == 0)
                {
                    await MarkArchiveFailedAsync(manifestId, "no_rows_to_archive", cancellationToken);
                    return false;
                }

                var uploadStarted = Stopwatch.GetTimestamp();
                await ExecuteWithRetryAsync(
                    async _ =>
                    {
                        await using var archiveStream = File.OpenRead(generation.FilePath);
                        await _archiveBlobContainer.SaveAsync(
                            blobName,
                            archiveStream,
                            overrideExisting: true,
                            cancellationToken: cancellationToken);
                    },
                    ResolveBlobRetryAttempts(options),
                    cancellationToken);
                UploadDurationMs.Record(Stopwatch.GetElapsedTime(uploadStarted).TotalMilliseconds);

                // Phase - completion: a separate fresh transactional scope records the archived manifest
                // after the blob upload above (which stays outside any database transaction) succeeds.
                await _uowExecutor.ExecuteTransactionalAsync(async ct =>
                {
                    var manifest = await _manifestRepository.GetAsync(manifestId, includeDetails: false, cancellationToken: ct);
                    manifest.MarkArchived(
                        archiveUri: archiveUri,
                        rowCount: generation.RowCount,
                        checksum: generation.ChecksumHex,
                        archivedAtUtc: DateTime.UtcNow,
                        deleteAfterUtc: deleteAfterUtc);
                    await _manifestRepository.UpdateAsync(manifest, autoSave: false, cancellationToken: ct);
                }, cancellationToken);

                RowsArchivedCounter.Add(generation.RowCount, new KeyValuePair<string, object?>("partition_key", partition.PartitionKey));

                if (options.DeleteArchivedFromHotStore)
                {
                    try
                    {
                        var deleted = await DeletePartitionRowsAsync(partition, options.DeleteBatchSize, cancellationToken);
                        RowsDeletedCounter.Add(deleted, new KeyValuePair<string, object?>("partition_key", partition.PartitionKey));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        FailureCounter.Add(
                            1,
                            new KeyValuePair<string, object?>("reason", "delete_failed"),
                            new KeyValuePair<string, object?>("stage", "hot_delete"));
                        _logger.LogWarning(
                            "Hot-store deletion failed for archive partition {PartitionKey}. Reason={ReasonCode}",
                            partition.PartitionKey,
                            ClassifyFailure(ex));
                    }
                }

                PartitionProcessDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return true;
            }
            finally
            {
                TryDeleteFile(generation.FilePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailureCounter.Add(
                1,
                new KeyValuePair<string, object?>("reason", ClassifyFailure(ex)),
                new KeyValuePair<string, object?>("stage", "archive"));
            _logger.LogWarning(
                "Archive processing failed for partition {PartitionKey}. Reason={ReasonCode}",
                partition.PartitionKey,
                ClassifyFailure(ex));

            if (manifestId > 0)
            {
                await MarkArchiveFailedAsync(manifestId, NormalizeFailureReason(ex), cancellationToken);
            }

            return false;
        }
    }

    private async Task<ArchiveFileGeneration> GenerateArchiveFileAsync(
        AuditArchivePartition partition,
        AuditLogArchiveOptions options,
        CancellationToken cancellationToken)
    {
        var tempFilePath = Path.Combine(
            Path.GetTempPath(),
            $"auditlogs-archive-{partition.PartitionKey.Replace(':', '-')}-{Guid.NewGuid():N}.jsonl.gz");

        long rowCount = 0;
        long? lastId = null;

        await using (var file = File.Create(tempFilePath))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: false))
        await using (var writer = new StreamWriter(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = await LoadPartitionRowsAsync(partition, lastId, options.RowBatchSize, cancellationToken);
                if (rows.Count == 0)
                {
                    break;
                }

                foreach (var row in rows)
                {
                    var record = ArchivedAuditLogRecord.FromEntity(row);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonOptions));
                    rowCount++;
                }

                lastId = rows[^1].Id;
            }

            await writer.FlushAsync(cancellationToken);
        }

        var checksumHex = await ComputeFileChecksumHexAsync(tempFilePath, cancellationToken);
        return new ArchiveFileGeneration
        {
            FilePath = tempFilePath,
            RowCount = rowCount,
            ChecksumHex = checksumHex
        };
    }

    private async Task<List<AuditLog>> LoadPartitionRowsAsync(
        AuditArchivePartition partition,
        long? lastId,
        int maxResultCount,
        CancellationToken cancellationToken)
    {
        var filter = new Domain.Shared.Auditing.AuditLogFilter
        {
            TenantId = partition.TenantId,
            FromUtc = partition.StartUtc,
            ToUtc = partition.EndUtc.AddTicks(-1)
        };

        return await _uowExecutor.ExecuteNonTransactionalAsync(async ct =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            var query = AuditLogQueryBuilder.Build(dbContext, filter, includeDetails: true);
            query = query.AsSplitQuery();
            if (lastId.HasValue)
            {
                query = query.Where(x => x.Id > lastId.Value);
            }

            return await query
                .OrderBy(x => x.Id)
                .Take(maxResultCount)
                .ToListAsync(ct);
        }, cancellationToken);
    }

    internal async Task<long> DeletePartitionRowsAsync(
        AuditArchivePartition partition,
        int deleteBatchSize,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var deleted = 0L;
        var skippedForHold = 0L;
        var lastScannedId = 0L;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await _uowExecutor.ExecuteTransactionalAsync(async ct =>
            {
                var dbContext = await _dbContextProvider.GetDbContextAsync();
                var ids = await dbContext.AuditLogs
                    .AsNoTracking()
                    .Where(x =>
                        x.Id > lastScannedId &&
                        x.ExecutionTime >= partition.StartUtc &&
                        x.ExecutionTime < partition.EndUtc &&
                        (partition.TenantId.HasValue
                            ? x.TenantId == partition.TenantId.Value
                            : x.TenantId == null))
                    .OrderBy(x => x.Id)
                    .Select(x => x.Id)
                    .Take(deleteBatchSize)
                    .ToListAsync(ct);

                if (ids.Count == 0)
                {
                    return DeleteBatchResult.Empty;
                }

                // Legal hold check (sub-flow 5): rows covered by an active hold are excluded from this
                // batch and never deleted, even though they're already archived.
                var deletableIds = await _legalHoldService.FilterOutHeldRowsAsync(ids, ct);
                var skipped = ids.Count - deletableIds.Count;
                var batchDeleted = 0L;

                if (deletableIds.Count > 0)
                {
                    batchDeleted = await dbContext.DeleteAuditLogsByIdsAsync(deletableIds, ct);
                }

                return new DeleteBatchResult(ids[^1], ids.Count, batchDeleted, skipped);
            }, cancellationToken);

            if (batch.CandidateCount == 0)
            {
                break;
            }

            lastScannedId = batch.LastScannedId;
            deleted += batch.DeletedCount;
            skippedForHold += batch.SkippedForHoldCount;

            if (batch.CandidateCount < deleteBatchSize)
            {
                break;
            }
        }

        if (skippedForHold > 0)
        {
            _logger.LogInformation(
                "Skipped deleting {SkippedCount} legally-held row(s) for archive partition {PartitionKey}.",
                skippedForHold,
                partition.PartitionKey);
        }

        DeleteDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return deleted;
    }

    private readonly record struct DeleteBatchResult(
        long LastScannedId,
        int CandidateCount,
        long DeletedCount,
        long SkippedForHoldCount)
    {
        public static DeleteBatchResult Empty { get; } = new(0, 0, 0, 0);
    }

}
