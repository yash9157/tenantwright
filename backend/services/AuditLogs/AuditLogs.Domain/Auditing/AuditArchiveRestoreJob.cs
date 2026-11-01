using AuditLogs.Domain.Shared.Auditing;
using Shared.Domain.Entities.Auditing;

namespace AuditLogs.Domain.Auditing;

public sealed class AuditArchiveRestoreJob : CreationAuditedAggregateRoot<long>
{
    public long ManifestId { get; private set; }
    public string PartitionKey { get; private set; } = default!;
    public AuditArchiveRestoreJobStatus Status { get; private set; }
    public string? Reason { get; private set; }
    public string? RequestedByUserId { get; private set; }
    public DateTime RequestedAtUtc { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public long? RestoredRows { get; private set; }
    public string? FailureReason { get; private set; }
    public string IdempotencyKey { get; private set; } = default!;

    private AuditArchiveRestoreJob()
    {
    }

    public AuditArchiveRestoreJob(
        long id,
        long manifestId,
        string partitionKey,
        string? reason,
        string? requestedByUserId,
        DateTime requestedAtUtc,
        string idempotencyKey)
        : base(id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(manifestId);

        ManifestId = manifestId;
        PartitionKey = global::Shared.Check.NotNullOrWhiteSpace(
            partitionKey,
            nameof(partitionKey),
            AuditLogConsts.MaxArchivePartitionKeyLength);
        Reason = NormalizeOptional(reason, AuditLogConsts.MaxArchiveFailureReasonLength);
        RequestedByUserId = NormalizeOptional(requestedByUserId, AuditLogConsts.MaxUserIdLength);
        RequestedAtUtc = NormalizeUtc(requestedAtUtc);
        IdempotencyKey = global::Shared.Check.NotNullOrWhiteSpace(
            idempotencyKey,
            nameof(idempotencyKey),
            AuditLogConsts.MaxArchiveRestoreIdempotencyKeyLength);
        Status = AuditArchiveRestoreJobStatus.Queued;
    }

    public void MarkRunning(DateTime startedAtUtc)
    {
        if (Status != AuditArchiveRestoreJobStatus.Queued)
        {
            throw new InvalidOperationException("Only a queued restore job can start.");
        }

        Status = AuditArchiveRestoreJobStatus.Running;
        StartedAtUtc = NormalizeUtc(startedAtUtc);
        CompletedAtUtc = null;
        FailureReason = null;
    }

    public void MarkCompleted(DateTime completedAtUtc, long restoredRows)
    {
        if (Status != AuditArchiveRestoreJobStatus.Running)
        {
            throw new InvalidOperationException("Only a running restore job can complete.");
        }

        Status = AuditArchiveRestoreJobStatus.Completed;
        CompletedAtUtc = NormalizeUtc(completedAtUtc);
        RestoredRows = Math.Max(0, restoredRows);
        FailureReason = null;
    }

    public void MarkFailed(DateTime completedAtUtc, string? failureReason)
    {
        if (Status is not (AuditArchiveRestoreJobStatus.Queued or AuditArchiveRestoreJobStatus.Running))
        {
            throw new InvalidOperationException("Only an active restore job can fail.");
        }

        Status = AuditArchiveRestoreJobStatus.Failed;
        CompletedAtUtc = NormalizeUtc(completedAtUtc);
        FailureReason = NormalizeOptional(failureReason, AuditLogConsts.MaxArchiveFailureReasonLength)
            ?? "Archive restore failed.";
    }

    public void Cancel(DateTime completedAtUtc)
    {
        if (Status != AuditArchiveRestoreJobStatus.Queued)
        {
            throw new InvalidOperationException("Only a queued restore job can be cancelled.");
        }

        Status = AuditArchiveRestoreJobStatus.Cancelled;
        CompletedAtUtc = NormalizeUtc(completedAtUtc);
        FailureReason = null;
    }

    public void Requeue(DateTime requestedAtUtc)
    {
        if (Status is not (AuditArchiveRestoreJobStatus.Failed or AuditArchiveRestoreJobStatus.Cancelled))
        {
            throw new InvalidOperationException("Only a failed or cancelled restore job can be retried.");
        }

        Status = AuditArchiveRestoreJobStatus.Queued;
        RequestedAtUtc = NormalizeUtc(requestedAtUtc);
        StartedAtUtc = null;
        CompletedAtUtc = null;
        RestoredRows = null;
        FailureReason = null;
    }

    public void RecoverInterrupted(DateTime requestedAtUtc)
    {
        if (Status != AuditArchiveRestoreJobStatus.Running)
        {
            throw new InvalidOperationException("Only an interrupted running restore job can be recovered.");
        }

        Status = AuditArchiveRestoreJobStatus.Queued;
        RequestedAtUtc = NormalizeUtc(requestedAtUtc);
        StartedAtUtc = null;
        CompletedAtUtc = null;
        RestoredRows = null;
        FailureReason = null;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
