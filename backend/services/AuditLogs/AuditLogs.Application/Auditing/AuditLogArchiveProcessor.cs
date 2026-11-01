using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
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
using Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.BlobStoring;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Repositories;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;

namespace AuditLogs.Application.Auditing;

public sealed partial class AuditLogArchiveProcessor : IAuditLogArchiveProcessor, ITransientDependency
{
    private const string ArchiveBlobPathPrefix = "audit-logs/archive/v1/";
    private const int MaxFailureReasonLength = 256;
    private const int MaxFailureAttemptsBeforePoison = 5;
    private const int RecoveryBatchSize = 100;
    private const string MaintenanceLockName = "auditlogs:archive-maintenance";

    private static readonly DistributedLockAcquireOptions MaintenanceLockOptions = new()
    {
        AcquireTimeoutMs = 0,
        LeaseMs = 60000,
        FailureMode = DistributedLockFailureMode.FailClosed,
        IgnoreTenant = true
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("AuditLogs.Archive");
    private static readonly Histogram<double> PartitionProcessDurationMs =
        Meter.CreateHistogram<double>("audit.archive.partition.process.duration.ms");
    private static readonly Histogram<double> UploadDurationMs =
        Meter.CreateHistogram<double>("audit.archive.upload.duration.ms");
    private static readonly Histogram<double> DeleteDurationMs =
        Meter.CreateHistogram<double>("audit.archive.delete.duration.ms");
    private static readonly Histogram<double> RestoreDurationMs =
        Meter.CreateHistogram<double>("audit.archive.restore.duration.ms");
    private static readonly Counter<long> RowsArchivedCounter =
        Meter.CreateCounter<long>("audit.archive.rows.archived");
    private static readonly Counter<long> RowsDeletedCounter =
        Meter.CreateCounter<long>("audit.archive.rows.deleted");
    private static readonly Counter<long> RowsRestoredCounter =
        Meter.CreateCounter<long>("audit.archive.rows.restored");
    private static readonly Counter<long> RestoreRequestsCounter =
        Meter.CreateCounter<long>("audit.archive.restore.requests");
    private static readonly Counter<long> FailureCounter =
        Meter.CreateCounter<long>("audit.archive.failures");

    private readonly IRepository<AuditArchiveManifest, long> _manifestRepository;
    private readonly IRepository<AuditArchiveRestoreJob, long> _restoreJobRepository;
    private readonly IBlobContainer<AuditLogArchiveBlobContainer> _archiveBlobContainer;
    private readonly IDbContextProvider<AuditLogsDbContextApp> _dbContextProvider;
    private readonly IUnitOfWorkExecutor _uowExecutor;
    private readonly IDistributedLock _distributedLock;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDataFilter _dataFilter;
    private readonly IOptionsMonitor<AuditLogArchiveOptions> _archiveOptions;
    private readonly AuditLogLegalHoldService _legalHoldService;
    private readonly ILogger<AuditLogArchiveProcessor> _logger;

    public AuditLogArchiveProcessor(
        IRepository<AuditArchiveManifest, long> manifestRepository,
        IRepository<AuditArchiveRestoreJob, long> restoreJobRepository,
        IBlobContainer<AuditLogArchiveBlobContainer> archiveBlobContainer,
        IDbContextProvider<AuditLogsDbContextApp> dbContextProvider,
        IUnitOfWorkExecutor uowExecutor,
        IDistributedLock distributedLock,
        ICurrentTenant currentTenant,
        IDataFilter dataFilter,
        IOptionsMonitor<AuditLogArchiveOptions> archiveOptions,
        AuditLogLegalHoldService legalHoldService,
        ILogger<AuditLogArchiveProcessor> logger)
    {
        _manifestRepository = manifestRepository;
        _restoreJobRepository = restoreJobRepository;
        _archiveBlobContainer = archiveBlobContainer;
        _dbContextProvider = dbContextProvider;
        _uowExecutor = uowExecutor;
        _distributedLock = distributedLock;
        _currentTenant = currentTenant;
        _dataFilter = dataFilter;
        _archiveOptions = archiveOptions;
        _legalHoldService = legalHoldService;
        _logger = logger;
    }

    private static AuditLog ToEntity(ArchivedAuditLogRecord record)
    {
        var auditLog = new AuditLog(
            id: 0,
            eventId: record.EventId,
            applicationName: record.ApplicationName,
            category: record.Category,
            tenantId: record.TenantId,
            tenantName: record.TenantName,
            userId: record.UserId,
            userName: record.UserName,
            executionTime: record.ExecutionTime,
            executionDuration: record.ExecutionDuration,
            clientIpAddress: record.ClientIpAddress,
            clientName: record.ClientName,
            clientId: record.ClientId,
            correlationId: record.CorrelationId,
            traceId: record.TraceId,
            browserInfo: record.BrowserInfo,
            httpMethod: record.HttpMethod,
            url: record.Url,
            httpStatusCode: record.HttpStatusCode,
            impersonatorUserId: record.ImpersonatorUserId,
            impersonatorUserName: record.ImpersonatorUserName,
            impersonatorTenantId: record.ImpersonatorTenantId,
            impersonatorTenantName: record.ImpersonatorTenantName,
            exceptions: record.Exceptions,
            comments: record.Comments,
            extraProperties: record.ExtraProperties,
            signature: record.Signature,
            signatureKeyId: record.SignatureKeyId,
            signatureTimestampUtc: record.SignatureTimestampUtc);

        foreach (var actionRecord in record.Actions)
        {
            var action = new AuditLogAction(
                id: 0,
                auditLogId: 0,
                tenantId: actionRecord.TenantId ?? record.TenantId,
                serviceName: actionRecord.ServiceName,
                methodName: actionRecord.MethodName,
                parameters: actionRecord.Parameters,
                executionTime: actionRecord.ExecutionTime,
                executionDuration: actionRecord.ExecutionDuration,
                result: actionRecord.Result);
            auditLog.Actions.Add(action);
        }

        foreach (var changeRecord in record.EntityChanges)
        {
            var entityChange = new EntityChange(
                id: 0,
                auditLogId: 0,
                tenantId: changeRecord.TenantId ?? record.TenantId,
                changeTime: changeRecord.ChangeTime,
                changeType: changeRecord.ChangeType,
                entityTenantId: changeRecord.EntityTenantId,
                entityId: changeRecord.EntityId,
                entityTypeFullName: changeRecord.EntityTypeFullName);

            foreach (var propertyRecord in changeRecord.PropertyChanges)
            {
                var propertyChange = new EntityPropertyChange(
                    id: 0,
                    entityChangeId: 0,
                    tenantId: propertyRecord.TenantId ?? record.TenantId,
                    propertyName: propertyRecord.PropertyName,
                    propertyTypeFullName: propertyRecord.PropertyTypeFullName,
                    originalValue: propertyRecord.OriginalValue,
                    newValue: propertyRecord.NewValue);
                entityChange.PropertyChanges.Add(propertyChange);
            }

            auditLog.EntityChanges.Add(entityChange);
        }

        return auditLog;
    }

    private async Task MarkArchiveFailedAsync(long manifestId, string? reason, CancellationToken cancellationToken)
    {
        await _uowExecutor.ExecuteTransactionalAsync(async ct =>
        {
            var manifest = await _manifestRepository.GetAsync(manifestId, includeDetails: false, cancellationToken: ct);
            IncrementFailureCount(manifest, "ArchiveFailureCount");
            manifest.MarkArchiveFailed(reason);
            await _manifestRepository.UpdateAsync(manifest, autoSave: false, cancellationToken: ct);
        }, cancellationToken);
    }

    private static string BuildBlobName(AuditArchivePartition partition)
    {
        var tenantSegment = partition.TenantId.HasValue ? partition.TenantId.Value.ToString("D") : "host";
        return $"audit-logs/archive/v1/tenant-{tenantSegment}/year={partition.Year:0000}/month={partition.Month:00}/partition-{partition.PartitionKey}.jsonl.gz";
    }

    private static string BuildArchiveUri(AuditLogArchiveOptions options, string blobName)
    {
        var containerName = string.IsNullOrWhiteSpace(options.Blob.ContainerName)
            ? AuditLogArchiveBlobContainer.Name
            : options.Blob.ContainerName.Trim();
        return $"blob://{containerName}/{blobName}";
    }

    private static (string Container, string BlobName) ParseArchiveUri(string archiveUri, AuditLogArchiveOptions options)
    {
        if (!Uri.TryCreate(archiveUri, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, "blob", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid archive URI.");
        }

        var container = parsed.Host.Trim();
        var expectedContainer = string.IsNullOrWhiteSpace(options.Blob.ContainerName)
            ? AuditLogArchiveBlobContainer.Name
            : options.Blob.ContainerName.Trim();
        if (!string.Equals(container, expectedContainer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Archive URI container mismatch.");
        }

        var blobName = parsed.AbsolutePath.TrimStart('/');
        if (string.IsNullOrWhiteSpace(blobName))
        {
            throw new InvalidOperationException("Archive URI does not contain a blob name.");
        }

        if (!blobName.StartsWith(ArchiveBlobPathPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Archive URI path is outside allowed prefix.");
        }

        return (container, blobName);
    }

    private static async Task<string> ComputeFileChecksumHexAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var checksum = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(checksum);
    }

    private static bool FixedTimeEqualsHex(string leftHex, string rightHex)
    {
        try
        {
            var left = Convert.FromHexString(leftHex);
            var right = Convert.FromHexString(rightHex);
            return CryptographicOperations.FixedTimeEquals(left, right);
        }
        catch
        {
            return false;
        }
    }

    private static string ClassifyFailure(Exception ex)
    {
        return ex switch
        {
            BlobNotFoundException => "blob_not_found",
            BlobConfigurationException => "blob_configuration",
            OperationCanceledException => "canceled",
            InvalidOperationException => "invalid_operation",
            _ => "unexpected"
        };
    }

    private static void IncrementFailureCount(AuditArchiveManifest manifest, string counterKey)
    {
        var current = 0;
        if (manifest.ExtraProperties.TryGetValue(counterKey, out var value) &&
            value != null &&
            int.TryParse(value.ToString(), out var parsed))
        {
            current = parsed;
        }

        manifest.ExtraProperties[counterKey] = (current + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsPoisonFailure(AuditArchiveManifest manifest, string counterKey)
    {
        if (!manifest.ExtraProperties.TryGetValue(counterKey, out var value) || value == null)
        {
            return false;
        }

        return int.TryParse(value.ToString(), out var parsed) &&
               parsed >= MaxFailureAttemptsBeforePoison;
    }

    private static string NormalizeFailureReason(Exception ex)
    {
        string reason = ex switch
        {
            BlobNotFoundException => "Archive blob was not found.",
            BlobConfigurationException => "Archive blob configuration is invalid.",
            OperationCanceledException => "Archive operation was canceled.",
            InvalidOperationException invalidOperationException => SanitizeFailureText(invalidOperationException.Message),
            _ => "Archive operation failed unexpectedly."
        };

        if (reason.Length > MaxFailureReasonLength)
        {
            reason = reason[..MaxFailureReasonLength];
        }

        return reason;
    }

    private static string SanitizeFailureText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Archive operation failed.";
        }

        var sanitized = value.Trim().ReplaceLineEndings(" ");
        if (ContainsSensitiveTokens(sanitized))
        {
            return "Archive operation failed due to a protected storage error.";
        }

        if (sanitized.Length > MaxFailureReasonLength)
        {
            sanitized = sanitized[..MaxFailureReasonLength];
        }

        return sanitized;
    }

    private static bool ContainsSensitiveTokens(string value)
    {
        var normalized = value.ToUpperInvariant();
        return normalized.Contains("PASSWORD", StringComparison.Ordinal) ||
               normalized.Contains("SECRET", StringComparison.Ordinal) ||
               normalized.Contains("TOKEN", StringComparison.Ordinal) ||
               normalized.Contains("ACCESSKEY", StringComparison.Ordinal) ||
               normalized.Contains("CONNECTIONSTRING", StringComparison.Ordinal) ||
               normalized.Contains("SAS", StringComparison.Ordinal);
    }

    private static int ResolveBlobRetryAttempts(AuditLogArchiveOptions options)
    {
        return Math.Clamp(options.Blob.FileSystem.RetryCount, 1, 5);
    }

    private static async Task ExecuteWithRetryAsync(
        Func<int, Task> action,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        maxAttempts = Math.Max(1, maxAttempts);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await action(attempt);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
            {
                var delayMs = 100 * attempt;
                await Task.Delay(delayMs, cancellationToken);
            }
        }
    }

    private async Task<Stream> GetBlobWithRetryAsync(
        string blobName,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        Stream? stream = null;
        await ExecuteWithRetryAsync(
            async _ => stream = await _archiveBlobContainer.GetAsync(blobName, cancellationToken),
            maxAttempts,
            cancellationToken);
        return stream!;
    }

    // Shared-database-only: archive partitions, manifests and blob names have no tenant-database discriminator, so the
    // archive maintenance job archives the shared database only (see AuditLogArchiveMaintenanceBackgroundJob).
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

    private static bool IsAuditEventAlreadyRecorded(SharedConflictException exception)
        => string.Equals(exception.Code, "audit_event_already_recorded", StringComparison.Ordinal);

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // ignore cleanup errors
        }
    }
}

internal sealed class ArchivedAuditLogRecord
{
    public Guid EventId { get; set; }
    public Guid? TenantId { get; set; }
    public string ApplicationName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? TenantName { get; set; }
    public string? ImpersonatorUserId { get; set; }
    public string? ImpersonatorUserName { get; set; }
    public Guid? ImpersonatorTenantId { get; set; }
    public string? ImpersonatorTenantName { get; set; }
    public DateTime ExecutionTime { get; set; }
    public int ExecutionDuration { get; set; }
    public string? ClientIpAddress { get; set; }
    public string? ClientName { get; set; }
    public string? ClientId { get; set; }
    public string? CorrelationId { get; set; }
    public string? TraceId { get; set; }
    public string? BrowserInfo { get; set; }
    public string? HttpMethod { get; set; }
    public string? Url { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? Exceptions { get; set; }
    public string? Comments { get; set; }
    public string? Signature { get; set; }
    public string? SignatureKeyId { get; set; }
    public DateTime? SignatureTimestampUtc { get; set; }
    public Dictionary<string, string?> ExtraProperties { get; set; } = new(StringComparer.Ordinal);
    public List<ArchivedAuditLogActionRecord> Actions { get; set; } = [];
    public List<ArchivedEntityChangeRecord> EntityChanges { get; set; } = [];

    public static ArchivedAuditLogRecord FromEntity(AuditLog auditLog)
    {
        return new ArchivedAuditLogRecord
        {
            EventId = auditLog.EventId,
            TenantId = auditLog.TenantId,
            ApplicationName = auditLog.ApplicationName,
            Category = auditLog.Category,
            UserId = auditLog.UserId,
            UserName = auditLog.UserName,
            TenantName = auditLog.TenantName,
            ImpersonatorUserId = auditLog.ImpersonatorUserId,
            ImpersonatorUserName = auditLog.ImpersonatorUserName,
            ImpersonatorTenantId = auditLog.ImpersonatorTenantId,
            ImpersonatorTenantName = auditLog.ImpersonatorTenantName,
            ExecutionTime = auditLog.ExecutionTime,
            ExecutionDuration = auditLog.ExecutionDuration,
            ClientIpAddress = auditLog.ClientIpAddress,
            ClientName = auditLog.ClientName,
            ClientId = auditLog.ClientId,
            CorrelationId = auditLog.CorrelationId,
            TraceId = auditLog.TraceId,
            BrowserInfo = auditLog.BrowserInfo,
            HttpMethod = auditLog.HttpMethod,
            Url = auditLog.Url,
            HttpStatusCode = auditLog.HttpStatusCode,
            Exceptions = auditLog.Exceptions,
            Comments = auditLog.Comments,
            Signature = auditLog.Signature,
            SignatureKeyId = auditLog.SignatureKeyId,
            SignatureTimestampUtc = auditLog.SignatureTimestampUtc,
            ExtraProperties = auditLog.ExtraProperties.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString(),
                StringComparer.Ordinal),
            Actions = auditLog.Actions
                .Select(action => new ArchivedAuditLogActionRecord
                {
                    TenantId = action.TenantId,
                    ServiceName = action.ServiceName,
                    MethodName = action.MethodName,
                    Parameters = action.Parameters,
                    ExecutionTime = action.ExecutionTime,
                    ExecutionDuration = action.ExecutionDuration,
                    Result = action.Result
                })
                .ToList(),
            EntityChanges = auditLog.EntityChanges
                .Select(entityChange => new ArchivedEntityChangeRecord
                {
                    TenantId = entityChange.TenantId,
                    ChangeTime = entityChange.ChangeTime,
                    ChangeType = entityChange.ChangeType,
                    EntityTenantId = entityChange.EntityTenantId,
                    EntityId = entityChange.EntityId,
                    EntityTypeFullName = entityChange.EntityTypeFullName,
                    PropertyChanges = entityChange.PropertyChanges
                        .Select(property => new ArchivedEntityPropertyChangeRecord
                        {
                            TenantId = property.TenantId,
                            PropertyName = property.PropertyName,
                            PropertyTypeFullName = property.PropertyTypeFullName,
                            OriginalValue = property.OriginalValue,
                            NewValue = property.NewValue
                        })
                        .ToList()
                })
                .ToList()
        };
    }
}

internal sealed class ArchivedAuditLogActionRecord
{
    public Guid? TenantId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string Parameters { get; set; } = "{}";
    public DateTime ExecutionTime { get; set; }
    public int ExecutionDuration { get; set; }
    public string? Result { get; set; }
}

internal sealed class ArchivedEntityChangeRecord
{
    public Guid? TenantId { get; set; }
    public DateTime ChangeTime { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public Guid? EntityTenantId { get; set; }
    public string EntityId { get; set; } = string.Empty;
    public string EntityTypeFullName { get; set; } = string.Empty;
    public List<ArchivedEntityPropertyChangeRecord> PropertyChanges { get; set; } = [];
}

internal sealed class ArchivedEntityPropertyChangeRecord
{
    public Guid? TenantId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string? PropertyTypeFullName { get; set; }
    public string? OriginalValue { get; set; }
    public string? NewValue { get; set; }
}

internal sealed class ArchivePartitionGroup
{
    public Guid? TenantId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
}

internal readonly record struct AuditArchivePartition(Guid? TenantId, int Year, int Month)
{
    public string PartitionKey => TenantId.HasValue
        ? $"tenant:{TenantId.Value:D}:{Year:0000}{Month:00}"
        : $"tenant:host:{Year:0000}{Month:00}";

    public DateTime StartUtc => new(Year, Month, 1, 0, 0, 0, DateTimeKind.Utc);

    public DateTime EndUtc => StartUtc.AddMonths(1);
}

internal sealed class ArchiveFileGeneration
{
    public required string FilePath { get; init; }
    public required long RowCount { get; init; }
    public required string ChecksumHex { get; init; }
}
