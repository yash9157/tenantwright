using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditArchiveManifestStatus = AuditLogs.Domain.Shared.Auditing.AuditArchiveManifestStatus;
using AuditLogConsts = AuditLogs.Domain.Shared.Auditing.AuditLogConsts;
using AuditLogFilter = AuditLogs.Domain.Shared.Auditing.AuditLogFilter;
using AuditLogExportFormat = AuditLogs.Domain.Shared.Auditing.AuditLogExportFormat;
using AuditLogExportStatus = AuditLogs.Domain.Shared.Auditing.AuditLogExportStatus;
using EntityChangeFilter = AuditLogs.Domain.Shared.Auditing.EntityChangeFilter;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared;
using Shared.BackgroundTasks.Jobs;
using Shared.AuditLogs.Auditing;
using Shared.AuditLogs.Publishing;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace AuditLogs.Application.Auditing;

[ExposeServices(typeof(IAuditLogQueryAppService))]
public sealed partial class AuditLogQueryAppService : IAuditLogQueryAppService, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("AuditLogs.Query");
    private static readonly Histogram<double> ListQueryDurationMs =
        Meter.CreateHistogram<double>("audit.query.list.duration.ms");
    private static readonly Histogram<double> EntityChangesQueryDurationMs =
        Meter.CreateHistogram<double>("audit.query.entity_changes.duration.ms");
    private static readonly Counter<long> ExportRequestCounter =
        Meter.CreateCounter<long>("audit.export.requests.created");
    private static readonly Counter<long> ExportDownloadCounter =
        Meter.CreateCounter<long>("audit.export.download.count");
    private static readonly Counter<long> ExportAccessDeniedCounter =
        Meter.CreateCounter<long>("audit.export.access.denied.count");
    private static readonly Histogram<double> ExportDownloadDurationMs =
        Meter.CreateHistogram<double>("audit.export.download.duration.ms");
    private static readonly Counter<long> ArchiveRestoreRequestCounter =
        Meter.CreateCounter<long>("audit.archive.restore.requests");

    private const int DefaultExportMaxRowCount = 50000;
    private const int MaxExportWindowDays = 31;
    private const int ExportTtlHours = 24;
    private static readonly TimeSpan ExportDownloadTokenLifetime = TimeSpan.FromMinutes(10);
    private const int ExportChunkSize = 1000;
    private const string AuditLogsDefaultQueue = "auditlogs-default";

    private readonly AuditLogQueryStore _queryStore;
    private readonly IRepository<AuditArchiveManifest, long> _archiveManifestRepository;
    private readonly IRepository<AuditArchiveRestoreJob, long> _archiveRestoreJobRepository;
    private readonly IRepository<AuditLogSealVerificationRun, long> _sealVerificationRunRepository;
    private readonly IRepository<AuditLogExport, long> _exportRepository;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly IDataFilter _dataFilter;
    private readonly IOptions<AuditLogSecurityOptions> _securityOptions;
    private readonly IOptions<AuditLogArchiveOptions> _archiveOptions;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly AuditLogEventSigner _signer;
    private readonly AuditLogSealService _sealService;
    private readonly TenantDatabaseFanOut _tenantDatabases;

    public AuditLogQueryAppService(
        AuditLogQueryStore queryStore,
        IRepository<AuditArchiveManifest, long> archiveManifestRepository,
        IRepository<AuditArchiveRestoreJob, long> archiveRestoreJobRepository,
        IRepository<AuditLogSealVerificationRun, long> sealVerificationRunRepository,
        IRepository<AuditLogExport, long> exportRepository,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser,
        IDataFilter dataFilter,
        IOptions<AuditLogSecurityOptions> securityOptions,
        IOptions<AuditLogArchiveOptions> archiveOptions,
        IBackgroundJobManager backgroundJobManager,
        AuditLogEventSigner signer,
        AuditLogSealService sealService,
        TenantDatabaseFanOut tenantDatabases)
    {
        _queryStore = queryStore;
        _archiveManifestRepository = archiveManifestRepository;
        _archiveRestoreJobRepository = archiveRestoreJobRepository;
        _sealVerificationRunRepository = sealVerificationRunRepository;
        _exportRepository = exportRepository;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _dataFilter = dataFilter;
        _securityOptions = securityOptions;
        _archiveOptions = archiveOptions;
        _backgroundJobManager = backgroundJobManager;
        _signer = signer;
        _sealService = sealService;
        _tenantDatabases = tenantDatabases;
    }

    private static SharedValidationException CreateValidationException(string code, string message)
        => new(code, message);

    private static SharedConflictException CreateConflictException(string code, string message)
        => new(code, message);

    private static EntityNotFoundException CreateNotFoundException(string code, string message)
        => new(code, message);

    private static AuditLogExportFilterSnapshot NormalizeExportSnapshot(CreateAuditLogExportInput input)
    {
        var fromUtc = NormalizeUtc(input.FromUtc);
        var toUtc = NormalizeUtc(input.ToUtc);
        if (fromUtc.HasValue && toUtc.HasValue && toUtc.Value < fromUtc.Value)
        {
            throw CreateValidationException(
                AuditLogsErrorCodes.InvalidDateRange,
                "ToUtc must be equal to or greater than FromUtc.");
        }

        if (fromUtc.HasValue && toUtc.HasValue && (toUtc.Value - fromUtc.Value) > TimeSpan.FromDays(MaxExportWindowDays))
        {
            throw CreateValidationException(
                AuditLogsErrorCodes.ExportWindowExceeded,
                $"Requested export date range cannot exceed {MaxExportWindowDays} days.");
        }

        return new AuditLogExportFilterSnapshot
        {
            TenantId = ResolveTenantFilter(input.TenantId),
            FromUtc = fromUtc,
            ToUtc = toUtc,
            UserId = NormalizeOptional(input.UserId, AuditLogConsts.MaxUserIdLength),
            ApplicationName = NormalizeOptional(input.ApplicationName, AuditLogConsts.MaxApplicationNameLength),
            Category = NormalizeOptional(input.Category, AuditLogConsts.MaxCategoryLength),
            UrlContains = NormalizeOptional(input.UrlContains, AuditLogConsts.MaxUrlLength),
            HttpStatusCode = input.HttpStatusCode,
            ClientIpAddress = NormalizeOptional(input.ClientIpAddress, AuditLogConsts.MaxClientIpAddressLength),
            CorrelationId = NormalizeOptional(input.CorrelationId, AuditLogConsts.MaxCorrelationIdLength),
            TraceId = NormalizeOptional(input.TraceId, AuditLogConsts.MaxTraceIdLength),
            MaxRowCount = Math.Clamp(input.MaxRowCount ?? DefaultExportMaxRowCount, 1, AuditLogConsts.MaxExportRowCount)
        };
    }

    private static Guid? ResolveTenantFilter(Guid? requestedTenantId)
    {
        return requestedTenantId;
    }

    private void EnsureHostArchiveAccess()
        => EnsureHostOperatorAccess();

    private void EnsureHostOperatorAccess()
    {
        if (_currentTenant.Id.HasValue || _currentUser.TenantId.HasValue)
        {
            throw new UnauthorizedAccessException("Audit console access is only available to host operators.");
        }
    }

    /// <summary>
    /// Host audit console access to the database the caller is in (the shared one in host scope), with the tenant
    /// filter lifted. Host-owned operational records (exports, archive manifests, restore jobs, seal verification
    /// runs) live only in the shared database, so their reads use this directly; audit rows are read through
    /// <see cref="QueryAuditDatabasesAsync{T}"/> so dedicated tenant databases are covered too.
    /// </summary>
    private async Task<T> ExecuteWithHostMultiTenantFilterDisabledAsync<T>(Func<Task<T>> action)
    {
        EnsureHostOperatorAccess();

        using (_dataFilter.Disable<IMultiTenant>())
        {
            return await action();
        }
    }

    /// <summary>
    /// Runs a host console read of audit rows in every database that can hold them: only the named tenant's database
    /// (its own when dedicated) when <paramref name="tenantId"/> is set, otherwise the shared database and every
    /// dedicated tenant database. Each call of <paramref name="query"/> runs in the visited database's scope and must
    /// restrict cross-tenant rows with <c>WhereOwnedByCurrentDatabase</c>; the caller merges the per-database results.
    /// </summary>
    private async Task<IReadOnlyList<T>> QueryAuditDatabasesAsync<T>(Guid? tenantId, string work, Func<Task<T>> query)
    {
        if (tenantId.HasValue)
        {
            return [await ExecuteInHostTargetTenantDatabaseAsync(tenantId.Value, query)];
        }

        return await ExecuteWithHostMultiTenantFilterDisabledAsync(() =>
            _tenantDatabases.QueryEveryDatabaseAsync<AuditLogsDbContextApp, T>(work, (_, _) => query()));
    }

    /// <summary>
    /// Runs a host console read of one tenant's audit rows in that tenant's database: its own scope when it has a
    /// dedicated database, otherwise the shared database. The tenant filter is lifted either way; every query carries
    /// the explicit tenant predicate or reads a row whose tenant the caller verifies.
    /// </summary>
    private async Task<T> ExecuteInHostTargetTenantDatabaseAsync<T>(Guid tenantId, Func<Task<T>> action)
    {
        EnsureHostOperatorAccess();

        using var tenantDatabase = _tenantDatabases.EnterDatabase(
            await _tenantDatabases.FindTenantDatabaseAsync<AuditLogsDbContextApp>(tenantId));
        using (_dataFilter.Disable<IMultiTenant>())
        {
            return await action();
        }
    }

    private void EnsureExportAccess(AuditLogExport export)
        => EnsureExportAccess(export, "cross_tenant");

    private void EnsureExportAccess(AuditLogExport export, string reason)
    {
        if (_currentTenant.Id.HasValue && export.TenantId != _currentTenant.Id)
        {
            ExportAccessDeniedCounter.Add(1, new KeyValuePair<string, object?>("reason", reason));
            throw new UnauthorizedAccessException("Cross-tenant export access is not allowed.");
        }
    }

    private void EnsureExportRequesterAccess(AuditLogExport export)
    {
        if (!_currentUser.Id.HasValue ||
            !Guid.TryParse(export.RequestedByUserId, out var requestedByUserId) ||
            requestedByUserId != _currentUser.Id.Value)
        {
            ExportAccessDeniedCounter.Add(1, new KeyValuePair<string, object?>("reason", "requester_mismatch"));
            throw new UnauthorizedAccessException("Only the export requester can issue a download token.");
        }
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (value.Value == default)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private static AuditArchiveManifestStatus? NormalizeArchiveStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<AuditArchiveManifestStatus>(value.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw CreateValidationException(
            AuditLogsErrorCodes.ArchiveStatusInvalid,
            "Archive status filter is invalid.");
    }

    private static DateTime? ParseUtcExtraProperty(ExtraPropertyDictionary extraProperties, string key)
    {
        if (!extraProperties.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        if (DateTime.TryParse(value.ToString(), out var parsed))
        {
            return parsed.Kind == DateTimeKind.Utc
                ? parsed
                : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return null;
    }

    private static long? ParseLongExtraProperty(ExtraPropertyDictionary extraProperties, string key)
    {
        if (!extraProperties.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return long.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static int? ParseIntExtraProperty(ExtraPropertyDictionary extraProperties, string key)
    {
        if (!extraProperties.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static bool ParseBoolExtraProperty(ExtraPropertyDictionary extraProperties, string key)
    {
        if (!extraProperties.TryGetValue(key, out var value) || value == null)
        {
            return false;
        }

        return bool.TryParse(value.ToString(), out var parsed) && parsed;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (normalized is { Length: > 0 } && normalized.Length > maxLength)
        {
            throw CreateValidationException(
                AuditLogsErrorCodes.InvalidFieldLength,
                $"Field length must be equal to or lower than {maxLength}.");
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static AuditLogExportFormat NormalizeExportFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return AuditLogExportFormat.Csv;
        }

        return format.Trim().ToLowerInvariant() switch
        {
            "csv" => AuditLogExportFormat.Csv,
            "jsonl" => AuditLogExportFormat.Jsonl,
            "ndjson" => AuditLogExportFormat.Jsonl,
            _ => throw CreateValidationException(
                AuditLogsErrorCodes.InvalidExportFormat,
                "Export format must be either 'csv' or 'jsonl'.")
        };
    }

    private static AuditLogExportJobDto MapExport(AuditLogExport export, string? downloadToken)
    {
        return new AuditLogExportJobDto
        {
            Id = export.Id,
            TenantId = export.TenantId,
            Status = export.Status.ToString().ToLowerInvariant(),
            Format = export.Format == AuditLogExportFormat.Csv ? "csv" : "jsonl",
            RequestedAtUtc = export.RequestedAtUtc,
            StartedAtUtc = export.StartedAtUtc,
            CompletedAtUtc = export.CompletedAtUtc,
            ExpireAtUtc = export.ExpireAtUtc,
            RowCount = export.RowCount,
            FileSizeBytes = export.FileSizeBytes,
            FailureReason = export.FailureReason,
            FileName = export.FileName,
            ContentType = export.ContentType,
            IsReadyForDownload = export.Status == AuditLogExportStatus.Completed && export.FileContent != null,
            DownloadToken = downloadToken
        };
    }

    private static string CreateDownloadToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static string ComputeTokenHash(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }

    private static bool IsTokenValid(string expectedHashHex, string token)
    {
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(expectedHashHex);
        }
        catch
        {
            return false;
        }

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }

    private static byte[] Decompress(byte[] compressedBytes)
    {
        using var compressed = new MemoryStream(compressedBytes);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static BackgroundJobEnqueueOptions CreateJobOptions(Guid? tenantId, string? requestedByUserId)
    {
        _ = Guid.TryParse(requestedByUserId, out var parsedRequestedByUserId);
        return new BackgroundJobEnqueueOptions
        {
            Queue = AuditLogsDefaultQueue,
            TenantId = tenantId,
            RequestedByUserId = parsedRequestedByUserId == Guid.Empty ? null : parsedRequestedByUserId
        };
    }

    private List<AuditLogExceptionDto> DeserializeExceptions(string? payload)
    {
        var security = _securityOptions.Value;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<AuditLogExceptionDto>>(payload, JsonOptions) ?? [];
            return ApplyExceptionExposure(parsed, security);
        }
        catch
        {
            return ApplyExceptionExposure(
                [
                    new AuditLogExceptionDto
                    {
                    Type = "unknown",
                    Message = payload.Trim()
                    }
                ],
                security);
        }
    }

    private static List<string> DeserializeComments(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        try
        {
            var comments = JsonSerializer.Deserialize<List<string>>(payload, JsonOptions);
            if (comments == null)
            {
                return [];
            }

            return comments
                .Where(comment => !string.IsNullOrWhiteSpace(comment))
                .Select(comment => comment.Trim())
                .ToList();
        }
        catch
        {
            // Backward-compatible parse path for newline-joined comments.
            return payload
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(comment => comment.Trim())
                .Where(comment => comment.Length > 0)
                .ToList();
        }
    }

    private static List<AuditLogExceptionDto> ApplyExceptionExposure(
        IReadOnlyList<AuditLogExceptionDto> exceptions,
        AuditLogSecurityOptions options)
    {
        if (options.SendExceptionDetailsToClients)
        {
            return exceptions
                .Select(exception => new AuditLogExceptionDto
                {
                    Type = options.SendExceptionTypeToClients ? exception.Type : "error",
                    Message = exception.Message,
                    StackTrace = options.SendExceptionStackTraceToClients ? exception.StackTrace : null
                })
                .ToList();
        }

        return exceptions
            .Select(_ => new AuditLogExceptionDto
            {
                Type = options.SendExceptionTypeToClients ? "unknown" : "error",
                Message = options.HiddenExceptionMessage,
                StackTrace = null
            })
            .ToList();
    }
}

internal sealed class AuditLogExportRow
{
    public long Id { get; set; }

    public Guid EventId { get; set; }

    public Guid? TenantId { get; set; }

    public DateTime ExecutionTime { get; set; }

    public int ExecutionDuration { get; set; }

    public string ApplicationName { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string? UserId { get; set; }

    public string? UserName { get; set; }

    public string? HttpMethod { get; set; }

    public string? Url { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientIpAddress { get; set; }
}
