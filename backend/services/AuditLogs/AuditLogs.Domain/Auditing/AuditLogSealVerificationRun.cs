using AuditLogs.Domain.Shared.Auditing;
using Shared.Domain.Entities.Auditing;

namespace AuditLogs.Domain.Auditing;

public sealed class AuditLogSealVerificationRun : CreationAuditedAggregateRoot<long>
{
    public long FromId { get; private set; }
    public long ToId { get; private set; }
    public AuditLogSealVerificationRunStatus Status { get; private set; }
    public bool? ChainIntact { get; private set; }
    public int SealsChecked { get; private set; }
    public long RowsChecked { get; private set; }
    public string IssuesJson { get; private set; } = "[]";
    public string? RequestedByUserId { get; private set; }
    public DateTime RequestedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    private AuditLogSealVerificationRun()
    {
    }

    public AuditLogSealVerificationRun(
        long id,
        long fromId,
        long toId,
        string? requestedByUserId,
        DateTime requestedAtUtc)
        : base(id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromId);

        ArgumentOutOfRangeException.ThrowIfLessThan(toId, fromId);

        FromId = fromId;
        ToId = toId;
        RequestedByUserId = NormalizeOptional(requestedByUserId, AuditLogConsts.MaxUserIdLength);
        RequestedAtUtc = NormalizeUtc(requestedAtUtc);
        Status = AuditLogSealVerificationRunStatus.Queued;
    }

    public void MarkRunning()
    {
        if (Status != AuditLogSealVerificationRunStatus.Queued)
        {
            throw new InvalidOperationException("Only a queued verification run can start.");
        }

        Status = AuditLogSealVerificationRunStatus.Running;
    }

    public void MarkCompleted(
        DateTime completedAtUtc,
        bool chainIntact,
        int sealsChecked,
        long rowsChecked,
        string issuesJson)
    {
        if (Status != AuditLogSealVerificationRunStatus.Running)
        {
            throw new InvalidOperationException("Only a running verification run can complete.");
        }

        Status = AuditLogSealVerificationRunStatus.Completed;
        CompletedAtUtc = NormalizeUtc(completedAtUtc);
        ChainIntact = chainIntact;
        SealsChecked = Math.Max(0, sealsChecked);
        RowsChecked = Math.Max(0, rowsChecked);
        IssuesJson = string.IsNullOrWhiteSpace(issuesJson) ? "[]" : issuesJson;
    }

    public void MarkFailed(DateTime completedAtUtc, string issuesJson)
    {
        if (Status is not (AuditLogSealVerificationRunStatus.Queued or AuditLogSealVerificationRunStatus.Running))
        {
            throw new InvalidOperationException("Only an active verification run can fail.");
        }

        Status = AuditLogSealVerificationRunStatus.Failed;
        CompletedAtUtc = NormalizeUtc(completedAtUtc);
        ChainIntact = false;
        IssuesJson = string.IsNullOrWhiteSpace(issuesJson) ? "[]" : issuesJson;
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
