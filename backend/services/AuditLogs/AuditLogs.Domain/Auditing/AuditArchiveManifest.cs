using AuditLogs.Domain.Shared.Auditing;
using Shared.AuditLogs.Auditing;
using Shared.Domain.Entities.Auditing;

namespace AuditLogs.Domain.Auditing;

public class AuditArchiveManifest : CreationAuditedAggregateRoot<long>
{
    public string PartitionKey { get; private set; } = default!;

    public string ArchiveUri { get; private set; } = default!;

    public long RowCount { get; private set; }

    public string Checksum { get; private set; } = default!;

    public DateTime ArchivedAtUtc { get; private set; }

    public DateTime DeleteAfterUtc { get; private set; }

    public AuditArchiveManifestStatus Status { get; private set; }

    protected AuditArchiveManifest()
    {
    }

    public AuditArchiveManifest(
        long id,
        string partitionKey,
        DateTime archivedAtUtc,
        DateTime deleteAfterUtc)
        : base(id)
    {
        PartitionKey = global::Shared.Check.NotNullOrWhiteSpace(
            partitionKey,
            nameof(partitionKey),
            AuditLogConsts.MaxArchivePartitionKeyLength);
        ArchiveUri = "blob://pending";
        RowCount = 0;
        Checksum = "pending";
        ArchivedAtUtc = NormalizeUtc(archivedAtUtc);
        DeleteAfterUtc = NormalizeUtc(deleteAfterUtc);
        Status = AuditArchiveManifestStatus.PendingArchive;
    }

    public void MarkArchiving()
    {
        if (Status is AuditArchiveManifestStatus.Archiving or AuditArchiveManifestStatus.Deleted)
        {
            return;
        }

        if (Status is AuditArchiveManifestStatus.Restoring)
        {
            throw new InvalidOperationException("Manifest cannot enter archiving while restore is in progress.");
        }

        Status = AuditArchiveManifestStatus.Archiving;
        SetFailureReason(null);
    }

    public void MarkArchived(
        string archiveUri,
        long rowCount,
        string checksum,
        DateTime archivedAtUtc,
        DateTime deleteAfterUtc)
    {
        ArchiveUri = global::Shared.Check.NotNullOrWhiteSpace(
            archiveUri,
            nameof(archiveUri),
            AuditLogConsts.MaxArchiveUriLength);
        RowCount = Math.Max(0, rowCount);
        Checksum = global::Shared.Check.NotNullOrWhiteSpace(
            checksum,
            nameof(checksum),
            AuditLogConsts.MaxArchiveChecksumLength);
        ArchivedAtUtc = NormalizeUtc(archivedAtUtc);
        DeleteAfterUtc = NormalizeUtc(deleteAfterUtc);
        Status = AuditArchiveManifestStatus.Archived;
        SetFailureReason(null);
    }

    public void MarkArchiveFailed(string? reason)
    {
        Status = AuditArchiveManifestStatus.ArchiveFailed;
        SetFailureReason(reason);
    }

    public void RecoverInterruptedArchive()
    {
        if (Status != AuditArchiveManifestStatus.Archiving)
        {
            throw new InvalidOperationException("Only an interrupted archiving manifest can be recovered.");
        }

        Status = AuditArchiveManifestStatus.PendingArchive;
        SetFailureReason(null);
    }

    public void MarkRestoreQueued(string? reason = null)
    {
        if (Status is AuditArchiveManifestStatus.Deleted)
        {
            throw new InvalidOperationException("Deleted archive manifests cannot be restored.");
        }

        if (Status is not (AuditArchiveManifestStatus.Archived or
            AuditArchiveManifestStatus.Restored or
            AuditArchiveManifestStatus.RestoreFailed))
        {
            throw new InvalidOperationException("Restore can be queued only for archived manifests.");
        }

        ExtraProperties["RestorePreviousStatus"] = Status.ToString();
        Status = AuditArchiveManifestStatus.RestoreQueued;
        ExtraProperties["RestoreQueuedAtUtc"] = DateTime.UtcNow.ToString("O");
        if (!string.IsNullOrWhiteSpace(reason))
        {
            ExtraProperties["RestoreReason"] = reason.Trim();
        }
        SetFailureReason(null);
    }

    public void MarkRestoring()
    {
        if (Status != AuditArchiveManifestStatus.RestoreQueued)
        {
            throw new InvalidOperationException("Restore can only start from queued status.");
        }

        Status = AuditArchiveManifestStatus.Restoring;
        ExtraProperties["RestoreStartedAtUtc"] = DateTime.UtcNow.ToString("O");
        SetFailureReason(null);
    }

    public void MarkRestored(long restoredRows)
    {
        Status = AuditArchiveManifestStatus.Restored;
        ExtraProperties["LastRestoredAtUtc"] = DateTime.UtcNow.ToString("O");
        ExtraProperties["LastRestoredRows"] = Math.Max(0, restoredRows).ToString(System.Globalization.CultureInfo.InvariantCulture);
        SetFailureReason(null);
    }

    public void MarkRestoreFailed(string? reason)
    {
        Status = AuditArchiveManifestStatus.RestoreFailed;
        SetFailureReason(reason);
    }

    public void RecoverInterruptedRestore()
    {
        if (Status != AuditArchiveManifestStatus.Restoring)
        {
            throw new InvalidOperationException("Only an interrupted restoring manifest can be recovered.");
        }

        Status = AuditArchiveManifestStatus.RestoreQueued;
        ExtraProperties.Remove("RestoreStartedAtUtc");
        ExtraProperties["RestoreQueuedAtUtc"] = DateTime.UtcNow.ToString("O");
        SetFailureReason(null);
    }

    public void CancelRestore()
    {
        if (Status != AuditArchiveManifestStatus.RestoreQueued)
        {
            throw new InvalidOperationException("Only a queued restore can be cancelled.");
        }

        var previousStatus = AuditArchiveManifestStatus.Archived;
        if (ExtraProperties.TryGetValue("RestorePreviousStatus", out var value) &&
            Enum.TryParse<AuditArchiveManifestStatus>(value?.ToString(), ignoreCase: true, out var parsed) &&
            parsed is AuditArchiveManifestStatus.Archived or AuditArchiveManifestStatus.Restored or AuditArchiveManifestStatus.RestoreFailed)
        {
            previousStatus = parsed;
        }

        Status = previousStatus;
        ExtraProperties.Remove("RestorePreviousStatus");
        ExtraProperties.Remove("RestoreQueuedAtUtc");
        ExtraProperties.Remove("RestoreReason");
        SetFailureReason(null);
    }

    public void MarkDeleted()
    {
        Status = AuditArchiveManifestStatus.Deleted;
        ExtraProperties["DeletedAtUtc"] = DateTime.UtcNow.ToString("O");
        SetFailureReason(null);
    }

    private void SetFailureReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            ExtraProperties.Remove("LastFailureReason");
            return;
        }

        var normalized = reason.Trim();
        if (normalized.Length > AuditLogConsts.MaxArchiveFailureReasonLength)
        {
            normalized = normalized[..AuditLogConsts.MaxArchiveFailureReasonLength];
        }

        ExtraProperties["LastFailureReason"] = normalized;
        ExtraProperties["LastFailureAtUtc"] = DateTime.UtcNow.ToString("O");
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
}
