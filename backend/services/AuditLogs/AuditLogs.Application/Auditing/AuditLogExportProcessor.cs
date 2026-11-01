using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogFilter = AuditLogs.Domain.Shared.Auditing.AuditLogFilter;
using AuditLogExportFormat = AuditLogs.Domain.Shared.Auditing.AuditLogExportFormat;
using AuditLogExportStatus = AuditLogs.Domain.Shared.Auditing.AuditLogExportStatus;
using AuditLogs.Domain.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;

namespace AuditLogs.Application.Auditing;

public sealed class AuditLogExportProcessor : IAuditLogExportProcessor, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("AuditLogs.Export");
    private static readonly Histogram<double> GenerationDurationMs =
        Meter.CreateHistogram<double>("audit.export.generation.duration.ms");
    private static readonly Histogram<double> QueueLagMs =
        Meter.CreateHistogram<double>("audit.export.queue.lag.ms");
    private static readonly Counter<long> FailureCounter =
        Meter.CreateCounter<long>("audit.export.failures");

    private readonly IRepository<AuditLogExport, long> _exportRepository;
    private readonly IDbContextProvider<AuditLogsDbContextApp> _dbContextProvider;
    private readonly IUnitOfWorkExecutor _uowExecutor;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDataFilter _dataFilter;
    private readonly ILogger<AuditLogExportProcessor> _logger;

    public AuditLogExportProcessor(
        IRepository<AuditLogExport, long> exportRepository,
        IDbContextProvider<AuditLogsDbContextApp> dbContextProvider,
        IUnitOfWorkExecutor uowExecutor,
        ICurrentTenant currentTenant,
        IDataFilter dataFilter,
        ILogger<AuditLogExportProcessor> logger)
    {
        _exportRepository = exportRepository;
        _dbContextProvider = dbContextProvider;
        _uowExecutor = uowExecutor;
        _currentTenant = currentTenant;
        _dataFilter = dataFilter;
        _logger = logger;
    }

    public async Task ProcessJobAsync(long exportId, CancellationToken cancellationToken = default)
    {
        if (await ProcessOneAsync(exportId, cancellationToken))
        {
            await CleanupExpiredExportsAsync(cancellationToken);
            return;
        }

        using var exportRecordScope = EnterExportRecordScope();
        var export = await _uowExecutor.ExecuteNonTransactionalAsync(async ct =>
        {
            var queryable = await _exportRepository.GetQueryableAsync();
            return await queryable
                .AsNoTracking()
                .Where(x => x.Id == exportId)
                .Select(x => new { x.Status, x.FailureReason })
                .SingleOrDefaultAsync(ct);
        }, cancellationToken);

        if (export == null)
        {
            throw new InvalidOperationException($"Audit log export '{exportId}' was not found.");
        }

        if (export.Status == AuditLogExportStatus.Completed || export.Status == AuditLogExportStatus.Expired)
        {
            return;
        }

        throw new InvalidOperationException(export.FailureReason ?? $"Audit log export '{exportId}' was not processed.");
    }

    private async Task<bool> ProcessOneAsync(long exportId, CancellationToken cancellationToken)
    {
        var claimTime = DateTime.UtcNow;

        // Phase 1 - claim: a fresh transactional scope moves the export from Queued to Processing.
        // The tracked entity never leaves this scope; only an immutable snapshot of the values
        // generation needs is returned.
        ClaimedExport? claimed;
        using (EnterExportRecordScope())
        {
            claimed = await ClaimAsync(exportId, claimTime, cancellationToken);
        }

        if (claimed == null)
        {
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            // Phase 2 - generation: keyset reads (each its own non-transactional scope), CSV/JSONL
            // serialization, and compression all run outside any database transaction, in the job's tenant scope,
            // which is the database holding the exported tenant's audit rows (its own when dedicated).
            var snapshot = ParseFilterSnapshot(claimed.FilterSnapshotJson);
            var generated = await GenerateContentAsync(claimed, snapshot, cancellationToken);

            // Phase 3 - completion: a separate fresh transactional scope stores the generated content.
            using var completionScope = EnterExportRecordScope();
            var completed = await _uowExecutor.ExecuteTransactionalAsync(async ct =>
            {
                var completion = await _exportRepository.FindAsync(exportId, includeDetails: false, cancellationToken: ct);
                if (completion == null || completion.Status != AuditLogExportStatus.Processing)
                {
                    return false;
                }

                completion.MarkCompleted(
                    completedAtUtc: DateTime.UtcNow,
                    fileName: generated.FileName,
                    contentType: generated.ContentType,
                    fileContent: generated.CompressedBytes,
                    compressed: true,
                    fileSizeBytes: generated.UncompressedSizeBytes,
                    rowCount: generated.RowCount);

                await _exportRepository.UpdateAsync(completion, autoSave: false, cancellationToken: ct);
                return true;
            }, cancellationToken);

            if (completed)
            {
                GenerationDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            return completed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailureCounter.Add(1, new KeyValuePair<string, object?>("reason", ClassifyFailure(ex)));
            _logger.LogWarning(ex, "Audit log export generation failed for ExportId {ExportId}.", exportId);

            // Phase 4 - failure: recorded only after the failed generation phase has unwound, in its own
            // fresh transactional scope. Cancellation is intentionally excluded above so host shutdown
            // or queue cancellation propagates instead of terminally failing the export.
            using var failureScope = EnterExportRecordScope();
            await _uowExecutor.ExecuteTransactionalAsync(async ct =>
            {
                var failed = await _exportRepository.FindAsync(exportId, includeDetails: false, cancellationToken: ct);
                if (failed != null && failed.Status != AuditLogExportStatus.Expired)
                {
                    failed.MarkFailed(DateTime.UtcNow, SanitizeFailureReason(ex));
                    await _exportRepository.UpdateAsync(failed, autoSave: false, cancellationToken: ct);
                }
            }, cancellationToken);

            return false;
        }
    }

    /// <summary>
    /// Export records are host operational rows: the host console creates them in host scope, so they always live in
    /// the shared database whatever the exported tenant's topology. Record access therefore runs in host scope with
    /// the tenant filter lifted (lookups are by the record's id), while row generation stays in the job's tenant scope.
    /// </summary>
    private ExportRecordScope EnterExportRecordScope() =>
        new(_currentTenant.Change(null), _dataFilter.Disable<IMultiTenant>());

    private sealed class ExportRecordScope(IDisposable hostScope, IDisposable tenantFilter) : IDisposable
    {
        public void Dispose()
        {
            tenantFilter.Dispose();
            hostScope.Dispose();
        }
    }

    private Task<ClaimedExport?> ClaimAsync(long exportId, DateTime claimTime, CancellationToken cancellationToken) =>
        _uowExecutor.ExecuteTransactionalAsync<ClaimedExport?>(async ct =>
        {
            var export = await _exportRepository.FindAsync(exportId, includeDetails: false, cancellationToken: ct);
            if (export == null || export.Status != AuditLogExportStatus.Queued)
            {
                return null;
            }

            var dueReferenceUtc = export.NextAttemptTimeUtc ?? export.RequestedAtUtc;
            QueueLagMs.Record(Math.Max(0, (claimTime - dueReferenceUtc).TotalMilliseconds));

            export.MarkProcessing(claimTime);
            await _exportRepository.UpdateAsync(export, autoSave: false, cancellationToken: ct);
            return new ClaimedExport(export.Id, export.Format, export.ChunkSize, export.FilterSnapshotJson);
        }, cancellationToken);

    private async Task<GeneratedExport> GenerateContentAsync(
        ClaimedExport export,
        AuditLogExportFilterSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await using var writer = new StreamWriter(buffer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);

        if (export.Format == AuditLogExportFormat.Csv)
        {
            await writer.WriteLineAsync(
                "id,eventId,tenantId,executionTime,executionDuration,applicationName,category,userId,userName,httpMethod,url,httpStatusCode,correlationId,traceId,clientId,clientIpAddress");
        }

        var writtenRows = 0L;
        AuditLogExportCursor? cursor = null;
        while (writtenRows < snapshot.MaxRowCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var takeCount = (int)Math.Min(export.ChunkSize, snapshot.MaxRowCount - writtenRows);
            var rows = await LoadRowsAsync(snapshot, cursor, takeCount, cancellationToken);
            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                if (export.Format == AuditLogExportFormat.Csv)
                {
                    await writer.WriteLineAsync(ToCsvLine(row));
                }
                else
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
                }
            }

            writtenRows += rows.Count;
            var tail = rows[^1];
            cursor = new AuditLogExportCursor(tail.ExecutionTime, tail.Id);
        }

        await writer.FlushAsync(cancellationToken);
        var rawBytes = buffer.ToArray();
        var compressed = Compress(rawBytes);
        if (compressed.LongLength > AuditLogConsts.MaxExportFileContentBytes)
        {
            throw new InvalidOperationException(
                $"Export payload exceeds maximum allowed size ({AuditLogConsts.MaxExportFileContentBytes} bytes).");
        }

        var extension = export.Format == AuditLogExportFormat.Csv ? "csv" : "jsonl";
        var contentType = export.Format == AuditLogExportFormat.Csv ? "text/csv" : "application/x-ndjson";
        var fileName = $"audit-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{extension}";

        return new GeneratedExport
        {
            CompressedBytes = compressed,
            UncompressedSizeBytes = rawBytes.LongLength,
            RowCount = writtenRows,
            ContentType = contentType,
            FileName = fileName
        };
    }

    private async Task<IReadOnlyList<AuditLogExportRow>> LoadRowsAsync(
        AuditLogExportFilterSnapshot snapshot,
        AuditLogExportCursor? cursor,
        int maxResultCount,
        CancellationToken cancellationToken)
    {
        return await _uowExecutor.ExecuteNonTransactionalAsync(async ct =>
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            var query = AuditLogQueryBuilder.Build(
                dbContext,
                new AuditLogFilter
                {
                    TenantId = snapshot.TenantId,
                    FromUtc = snapshot.FromUtc,
                    ToUtc = snapshot.ToUtc,
                    UserId = snapshot.UserId,
                    ApplicationName = snapshot.ApplicationName,
                    Category = snapshot.Category,
                    UrlContains = snapshot.UrlContains,
                    HttpStatusCode = snapshot.HttpStatusCode,
                    ClientIpAddress = snapshot.ClientIpAddress,
                    CorrelationId = snapshot.CorrelationId,
                    TraceId = snapshot.TraceId
                },
                includeDetails: false);

            query = ApplyKeysetCursor(query, cursor);

            return await query
                .OrderByDescending(x => x.ExecutionTime)
                .ThenByDescending(x => x.Id)
                .Take(maxResultCount)
                .Select(x => new AuditLogExportRow
                {
                    Id = x.Id,
                    EventId = x.EventId,
                    TenantId = x.TenantId,
                    ExecutionTime = x.ExecutionTime,
                    ExecutionDuration = x.ExecutionDuration,
                    ApplicationName = x.ApplicationName,
                    Category = x.Category,
                    UserId = x.UserId,
                    UserName = x.UserName,
                    HttpMethod = x.HttpMethod,
                    Url = x.Url,
                    HttpStatusCode = x.HttpStatusCode,
                    CorrelationId = x.CorrelationId,
                    TraceId = x.TraceId,
                    ClientId = x.ClientId,
                    ClientIpAddress = x.ClientIpAddress
                })
                .ToListAsync(ct);
        }, cancellationToken);
    }

    internal static IQueryable<AuditLog> ApplyKeysetCursor(
        IQueryable<AuditLog> query,
        AuditLogExportCursor? cursor)
    {
        if (!cursor.HasValue)
        {
            return query;
        }

        var value = cursor.Value;
        return query.Where(x =>
            x.ExecutionTime < value.ExecutionTime ||
            (x.ExecutionTime == value.ExecutionTime && x.Id < value.Id));
    }

    private static AuditLogExportFilterSnapshot ParseFilterSnapshot(string payload)
    {
        var snapshot = JsonSerializer.Deserialize<AuditLogExportFilterSnapshot>(payload, JsonOptions)
                       ?? throw new InvalidOperationException("Audit export filter snapshot is invalid.");

        snapshot.MaxRowCount = Math.Clamp(
            snapshot.MaxRowCount <= 0 ? 1 : snapshot.MaxRowCount,
            1,
            AuditLogConsts.MaxExportRowCount);

        return snapshot;
    }

    private async Task CleanupExpiredExportsAsync(CancellationToken cancellationToken)
    {
        var utcNow = DateTime.UtcNow;

        // Phase 7 - cleanup: expired export rows are deleted in their own fresh transactional scope.
        using var exportRecordScope = EnterExportRecordScope();
        await _uowExecutor.ExecuteTransactionalAsync(
            ct => _exportRepository.DeleteAsync(
                x => x.ExpireAtUtc <= utcNow,
                autoSave: false,
                cancellationToken: ct),
            cancellationToken);
    }

    internal static string ToCsvLine(AuditLogExportRow row)
    {
        var values = new[]
        {
            row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            row.EventId.ToString("D"),
            row.TenantId?.ToString("D") ?? string.Empty,
            row.ExecutionTime.ToString("O"),
            row.ExecutionDuration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            row.ApplicationName,
            row.Category,
            row.UserId ?? string.Empty,
            row.UserName ?? string.Empty,
            row.HttpMethod ?? string.Empty,
            row.Url ?? string.Empty,
            row.HttpStatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            row.CorrelationId ?? string.Empty,
            row.TraceId ?? string.Empty,
            row.ClientId ?? string.Empty,
            row.ClientIpAddress ?? string.Empty
        };

        return string.Join(',', values.Select(EscapeCsv));
    }

    internal static string EscapeCsv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    internal static byte[] Compress(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    private static string SanitizeFailureReason(Exception ex)
    {
        var message = ex.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return "audit_export_failed";
        }

        var normalized = message.Trim();
        return normalized.Length <= AuditLogConsts.MaxExportFailureReasonLength
            ? normalized
            : normalized[..AuditLogConsts.MaxExportFailureReasonLength];
    }

    private static string ClassifyFailure(Exception ex)
    {
        return ex switch
        {
            InvalidOperationException => "invalid_operation",
            OperationCanceledException => "canceled",
            _ => "unexpected"
        };
    }

    private sealed record ClaimedExport(
        long ExportId,
        AuditLogExportFormat Format,
        int ChunkSize,
        string FilterSnapshotJson);

    private sealed class GeneratedExport
    {
        public required byte[] CompressedBytes { get; init; }

        public required long UncompressedSizeBytes { get; init; }

        public required long RowCount { get; init; }

        public required string ContentType { get; init; }

        public required string FileName { get; init; }
    }
}

internal readonly record struct AuditLogExportCursor(DateTime ExecutionTime, long Id);
