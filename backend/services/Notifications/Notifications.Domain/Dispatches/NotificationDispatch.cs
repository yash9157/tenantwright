using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.Dispatches;

public sealed class NotificationDispatch : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid ScopeId { get; private set; }
    public Guid RequestId { get; private set; }
    public string RequestFingerprint { get; private set; } = string.Empty;
    public string SourceService { get; private set; } = string.Empty;
    public NotificationDispatchMode Mode { get; private set; }
    public NotificationDispatchState State { get; private set; }
    public DateTime ActivationTimeUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? NextEvaluationAtUtc { get; private set; }
    public DateTime? CancelRequestedAtUtc { get; private set; }
    public long Revision { get; private set; }
    public ICollection<NotificationDispatchRecipient> Recipients { get; private set; } = [];
    public ICollection<NotificationDispatchEvent> Events { get; private set; } = [];

    private NotificationDispatch() { }

    public NotificationDispatch(Guid id, Guid? tenantId, Guid requestId, string fingerprint,
        string sourceService, NotificationDispatchMode mode, DateTime activationTimeUtc, DateTime expiresAtUtc,
        DateTime? admittedAtUtc = null)
        : base(id)
    {
        if (id == Guid.Empty || requestId == Guid.Empty || tenantId == Guid.Empty)
            throw new ArgumentException("Dispatch and request identities must be nonempty; host scope uses null.");
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (activationTimeUtc.Kind != DateTimeKind.Utc || expiresAtUtc.Kind != DateTimeKind.Utc ||
            expiresAtUtc <= activationTimeUtc || expiresAtUtc - activationTimeUtc > TimeSpan.FromDays(30))
            throw new ArgumentException("Dispatch activation and expiry must be UTC with a lifetime of at most 30 days.");
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("A canonical SHA-256 request fingerprint is required.", nameof(fingerprint));

        TenantId = tenantId;
        ScopeId = tenantId ?? Guid.Empty;
        RequestId = requestId;
        RequestFingerprint = fingerprint;
        SourceService = global::Shared.Check.NotNullOrWhiteSpace(sourceService, nameof(sourceService), maxLength: 128);
        Mode = mode;
        State = NotificationDispatchState.Pending;
        ActivationTimeUtc = activationTimeUtc;
        ExpiresAtUtc = expiresAtUtc;
        NextEvaluationAtUtc = activationTimeUtc;
        Record(NotificationDispatchEventKind.Admitted, admittedAtUtc ?? activationTimeUtc);
    }

    public void AddRecipient(NotificationDispatchRecipient recipient)
    {
        if (recipient.DispatchId != Id || recipient.ScopeId != ScopeId ||
            Recipients.Any(existing => existing.RecipientKey == recipient.RecipientKey))
            throw new InvalidOperationException("Dispatch recipients must have matching ownership and unique keys.");
        Recipients.Add(recipient);
    }

    public bool CanStartWork(DateTime nowUtc) =>
        CancelRequestedAtUtc is null && nowUtc >= ActivationTimeUtc && nowUtc < ExpiresAtUtc;

    public void RequestCancellation(DateTime nowUtc, string actorId)
    {
        if (nowUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(actorId) || actorId.Length > 128)
            throw new ArgumentException("Cancellation requires UTC time and a bounded operator identity.");
        if (CancelRequestedAtUtc.HasValue)
            return;
        CancelRequestedAtUtc = nowUtc;
        foreach (var step in Recipients.SelectMany(recipient => recipient.Steps))
        {
            // A scheduling claim may still be preparing an external dependency. The final scheduling gate checks
            // cancellation again before creating a child. A claimed provider send is not retractable.
            if (step.State is NotificationDispatchState.Pending or NotificationDispatchState.Scheduling || step.IsPreparationDeferred)
                step.Cancel();
            step.ScheduleEvaluation(nowUtc);
        }
        NextEvaluationAtUtc = nowUtc;
        Record(NotificationDispatchEventKind.CancelRequested, nowUtc, actorId: actorId);
        RefreshSummary();
    }

    public void ScheduleEvaluation(DateTime dueUtc)
    {
        if (dueUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Evaluation time must be UTC.", nameof(dueUtc));
        NextEvaluationAtUtc = dueUtc;
    }

    public void StopPolling() => NextEvaluationAtUtc = null;

    public void Record(NotificationDispatchEventKind kind, DateTime nowUtc,
        Guid? recipientId = null, Guid? stepId = null, string? reason = null, string? actorId = null)
    {
        if (nowUtc.Kind != DateTimeKind.Utc || !Enum.IsDefined(kind))
            throw new ArgumentException("Dispatch chronology requires a known event and UTC time.");
        Revision++;
        Events.Add(new NotificationDispatchEvent(Guid.CreateVersion7(), TenantId, Id, Revision,
            kind, nowUtc, recipientId, stepId, reason, actorId));
    }

    public void RefreshSummary()
    {
        foreach (var recipient in Recipients)
            recipient.RefreshSummary(Mode);
        State = NotificationDispatchOutcomes.Aggregate(Recipients.Select(recipient => recipient.State));
    }
}

public static class NotificationDispatchOutcomes
{
    public static NotificationDispatchState Aggregate(IEnumerable<NotificationDispatchState> outcomes)
    {
        var states = outcomes.ToArray();
        if (states.Length == 0)
            return NotificationDispatchState.Pending;
        if (states.Contains(NotificationDispatchState.NeedsReview))
            return NotificationDispatchState.NeedsReview;
        if (states.Any(state => state is NotificationDispatchState.Pending or NotificationDispatchState.Scheduling or
                NotificationDispatchState.Queued or NotificationDispatchState.Deferred))
            return states.Contains(NotificationDispatchState.Deferred) ? NotificationDispatchState.Deferred :
                states.Contains(NotificationDispatchState.Scheduling) ? NotificationDispatchState.Scheduling :
                states.Contains(NotificationDispatchState.Queued) ? NotificationDispatchState.Queued : NotificationDispatchState.Pending;
        if (states.All(state => state == NotificationDispatchState.Suppressed))
            return NotificationDispatchState.Suppressed;
        var eligible = states.Where(state => state != NotificationDispatchState.Suppressed).ToArray();
        if (eligible.All(state => state == NotificationDispatchState.Delivered))
            return NotificationDispatchState.Delivered;
        if (eligible.All(state => state is NotificationDispatchState.Delivered or NotificationDispatchState.Accepted))
            return NotificationDispatchState.Accepted;
        if (eligible.All(state => state == NotificationDispatchState.Cancelled))
            return NotificationDispatchState.Cancelled;
        if (eligible.All(state => state == NotificationDispatchState.Expired))
            return NotificationDispatchState.Expired;
        if (eligible.All(state => state == NotificationDispatchState.Rejected))
            return NotificationDispatchState.Rejected;
        return NotificationDispatchState.PartiallyCompleted;
    }
}
