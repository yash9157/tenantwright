using AuditLogs.Domain.Shared.Auditing;
using Shared.AuditLogs.Auditing;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;

namespace AuditLogs.Domain.Auditing;

public class AuditLogExport : CreationAuditedAggregateRoot<long>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public string? RequestedByUserId { get; private set; }

    public AuditLogExportStatus Status { get; private set; }

    public AuditLogExportFormat Format { get; private set; }

    public string FilterSnapshotJson { get; private set; } = default!;

    public int MaxRowCount { get; private set; }

    public int ChunkSize { get; private set; }

    public DateTime RequestedAtUtc { get; private set; }

    public DateTime? StartedAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public DateTime? NextAttemptTimeUtc { get; private set; }

    public int TryCount { get; private set; }

    public DateTime ExpireAtUtc { get; private set; }

    public string? DownloadTokenHash { get; private set; }

    public DateTime? DownloadTokenExpiresAtUtc { get; private set; }

    public string? FileName { get; private set; }

    public string? ContentType { get; private set; }

    public byte[]? FileContent { get; private set; }

    public bool FileContentCompressed { get; private set; }

    public long? FileSizeBytes { get; private set; }

    public long? RowCount { get; private set; }

    public string? FailureReason { get; private set; }

    protected AuditLogExport()
    {
    }

    public AuditLogExport(
        long id,
        Guid? tenantId,
        string? requestedByUserId,
        AuditLogExportFormat format,
        string filterSnapshotJson,
        int maxRowCount,
        int chunkSize,
        DateTime requestedAtUtc,
        DateTime expireAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        RequestedByUserId = NormalizeOptional(requestedByUserId, nameof(requestedByUserId), AuditLogConsts.MaxUserIdLength);
        Format = format;
        FilterSnapshotJson = global::Shared.Check.NotNullOrWhiteSpace(
            filterSnapshotJson,
            nameof(filterSnapshotJson),
            AuditLogConsts.MaxExportFilterSnapshotLength);
        MaxRowCount = Math.Clamp(maxRowCount, 1, AuditLogConsts.MaxExportRowCount);
        ChunkSize = Math.Clamp(chunkSize, 1, AuditLogConsts.MaxExportChunkSize);
        RequestedAtUtc = NormalizeUtc(requestedAtUtc);
        ExpireAtUtc = NormalizeUtc(expireAtUtc);
        Status = AuditLogExportStatus.Queued;
        NextAttemptTimeUtc = RequestedAtUtc;
    }

    public void MarkProcessing(DateTime startedAtUtc)
    {
        if (Status is AuditLogExportStatus.Completed or AuditLogExportStatus.Expired)
        {
            throw new InvalidOperationException("Export cannot be processed once completed or expired.");
        }

        Status = AuditLogExportStatus.Processing;
        StartedAtUtc = NormalizeUtc(startedAtUtc);
        NextAttemptTimeUtc = null;
        TryCount++;
        FailureReason = null;
    }

    public void MarkCompleted(
        DateTime completedAtUtc,
        string fileName,
        string contentType,
        byte[] fileContent,
        bool compressed,
        long fileSizeBytes,
        long rowCount)
    {
        if (fileContent.Length > AuditLogConsts.MaxExportFileContentBytes)
        {
            throw new ArgumentException(
                $"fileContent length must be equal to or lower than {AuditLogConsts.MaxExportFileContentBytes}.",
                nameof(fileContent));
        }

        Status = AuditLogExportStatus.Completed;
        CompletedAtUtc = NormalizeUtc(completedAtUtc);
        NextAttemptTimeUtc = null;
        FailureReason = null;
        FileName = global::Shared.Check.NotNullOrWhiteSpace(fileName, nameof(fileName), AuditLogConsts.MaxExportFileNameLength);
        ContentType = global::Shared.Check.NotNullOrWhiteSpace(contentType, nameof(contentType), AuditLogConsts.MaxExportContentTypeLength);
        FileContent = fileContent;
        FileContentCompressed = compressed;
        FileSizeBytes = Math.Max(0, fileSizeBytes);
        RowCount = Math.Max(0, rowCount);
    }

    public void MarkFailed(DateTime failedAtUtc, string? failureReason, TimeSpan? retryAfter = null)
    {
        Status = AuditLogExportStatus.Failed;
        CompletedAtUtc = NormalizeUtc(failedAtUtc);
        FailureReason = NormalizeOptional(
            failureReason,
            nameof(failureReason),
            AuditLogConsts.MaxExportFailureReasonLength);
        NextAttemptTimeUtc = retryAfter.HasValue
            ? NormalizeUtc(failedAtUtc).Add(retryAfter.Value)
            : null;
    }

    /// <summary>
    /// Cancels a not-yet-started export. Only a <see cref="AuditLogExportStatus.Queued"/> export can
    /// be cancelled; once <c>Processing</c> it is left to finish/fail to avoid racing the worker.
    /// </summary>
    public void MarkCancelled(DateTime cancelledAtUtc)
    {
        if (Status != AuditLogExportStatus.Queued)
        {
            throw new InvalidOperationException($"Only a queued export can be cancelled (current status: {Status}).");
        }

        Status = AuditLogExportStatus.Cancelled;
        CompletedAtUtc = NormalizeUtc(cancelledAtUtc);
        NextAttemptTimeUtc = null;
        FailureReason = null;
    }

    /// <summary>
    /// Re-queues a failed export for another attempt. Only a <see cref="AuditLogExportStatus.Failed"/>
    /// export can be retried; expired/completed/cancelled exports cannot (their content/TTL is gone).
    /// </summary>
    public void RequeueForRetry(DateTime requeuedAtUtc)
    {
        if (Status != AuditLogExportStatus.Failed)
        {
            throw new InvalidOperationException($"Only a failed export can be retried (current status: {Status}).");
        }

        Status = AuditLogExportStatus.Queued;
        NextAttemptTimeUtc = NormalizeUtc(requeuedAtUtc);
        CompletedAtUtc = null;
        FailureReason = null;
    }

    public void MarkExpired(DateTime expiredAtUtc)
    {
        Status = AuditLogExportStatus.Expired;
        ExpireAtUtc = NormalizeUtc(expiredAtUtc);
        NextAttemptTimeUtc = null;
        FileContent = null;
        FileName = null;
        ContentType = null;
        FileSizeBytes = null;
        RowCount = null;
        FailureReason = null;
    }

    public void ReplaceDownloadToken(string downloadTokenHash, DateTime expiresAtUtc)
    {
        DownloadTokenHash = global::Shared.Check.NotNullOrWhiteSpace(
            downloadTokenHash,
            nameof(downloadTokenHash),
            AuditLogConsts.MaxExportDownloadTokenHashLength);
        DownloadTokenExpiresAtUtc = NormalizeUtc(expiresAtUtc);
    }

    public void ConsumeDownloadToken(DateTime consumedAtUtc)
    {
        DownloadTokenHash = null;
        DownloadTokenExpiresAtUtc = null;
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
        {
            return DateTime.UtcNow;
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string? NormalizeOptional(string? value, string paramName, int maxLength)
    {
        var normalized = value?.Trim();
        if (normalized is { Length: > 0 } && normalized.Length > maxLength)
        {
            throw new ArgumentException(
                $"{paramName} length must be equal to or lower than {maxLength}.",
                paramName);
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
