using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Notifications.Domain.Dispatches;

public sealed class NotificationDispatchStep : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid ScopeId { get; private set; }
    public Guid DispatchId { get; private set; }
    public Guid RecipientId { get; private set; }
    public NotificationChannel Channel { get; private set; }
    public int Ordinal { get; private set; }
    public string ContentJson { get; private set; } = string.Empty;
    public NotificationDispatchState State { get; private set; } = NotificationDispatchState.Pending;
    public Guid? SchedulingClaimId { get; private set; }
    public DateTime? ClaimExpiresAtUtc { get; private set; }
    public DateTime NextEvaluationAtUtc { get; private set; }
    public string? OutcomeReason { get; private set; }
    public bool IsPreparationDeferred { get; private set; }

    private NotificationDispatchStep() { }

    public NotificationDispatchStep(Guid id, Guid? tenantId, Guid dispatchId, Guid recipientId,
        NotificationChannel channel, int ordinal, string contentJson, DateTime activationTimeUtc) : base(id)
    {
        if (id == Guid.Empty || dispatchId == Guid.Empty || recipientId == Guid.Empty || tenantId == Guid.Empty ||
            !Enum.IsDefined(channel) || ordinal is < 0 or > 4 || activationTimeUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Invalid dispatch step identity, channel, order or activation time.");
        TenantId = tenantId;
        ScopeId = tenantId ?? Guid.Empty;
        DispatchId = dispatchId;
        RecipientId = recipientId;
        Channel = channel;
        Ordinal = ordinal;
        ContentJson = global::Shared.Check.NotNullOrWhiteSpace(contentJson, nameof(contentJson), maxLength: 262144);
        NextEvaluationAtUtc = activationTimeUtc;
    }

    public bool IsDue(DateTime nowUtc) => (State == NotificationDispatchState.Pending ||
        State == NotificationDispatchState.Deferred && IsPreparationDeferred) && NextEvaluationAtUtc <= nowUtc;

    public void ScheduleEvaluation(DateTime dueUtc)
    {
        if (dueUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Evaluation time must be UTC.", nameof(dueUtc));
        NextEvaluationAtUtc = dueUtc;
    }

    public Guid ClaimScheduling(DateTime nowUtc)
    {
        if (!IsDue(nowUtc))
            throw new InvalidOperationException("Only a due pending channel step can be scheduled.");
        State = NotificationDispatchState.Scheduling;
        IsPreparationDeferred = false;
        SchedulingClaimId = Guid.CreateVersion7();
        ClaimExpiresAtUtc = nowUtc.AddSeconds(60);
        return SchedulingClaimId.Value;
    }

    public bool OwnsSchedulingClaim(Guid claimId, DateTime nowUtc) =>
        State == NotificationDispatchState.Scheduling && SchedulingClaimId == claimId && ClaimExpiresAtUtc > nowUtc;

    public void MarkScheduled()
    {
        if (State != NotificationDispatchState.Scheduling)
            throw new InvalidOperationException("A scheduling claim may link one channel notification.");
        State = NotificationDispatchState.Queued;
        SchedulingClaimId = null;
        ClaimExpiresAtUtc = null;
    }

    public void Observe(NotificationDispatchState outcome, string? reason = null)
    {
        if (!Enum.IsDefined(outcome) || outcome is NotificationDispatchState.Pending or
            NotificationDispatchState.Scheduling or NotificationDispatchState.PartiallyCompleted)
            throw new ArgumentOutOfRangeException(nameof(outcome));
        if (State == NotificationDispatchState.Delivered && outcome != NotificationDispatchState.Delivered)
        {
            State = NotificationDispatchState.NeedsReview;
            OutcomeReason = "contradictory_delivery_evidence";
            return;
        }
        if (State == NotificationDispatchState.NeedsReview && OutcomeReason == "contradictory_delivery_evidence")
            return;
        State = outcome;
        IsPreparationDeferred = false;
        OutcomeReason = reason is null ? null : global::Shared.Check.NotNullOrWhiteSpace(reason, nameof(reason), maxLength: 128);
        SchedulingClaimId = null;
        ClaimExpiresAtUtc = null;
    }

    public void ReleaseScheduling(DateTime dueUtc, string reason)
    {
        if (State != NotificationDispatchState.Scheduling || dueUtc.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("Only an unlinked scheduling claim can be released.");
        State = NotificationDispatchState.Pending;
        NextEvaluationAtUtc = dueUtc;
        SchedulingClaimId = null;
        ClaimExpiresAtUtc = null;
        OutcomeReason = global::Shared.Check.NotNullOrWhiteSpace(reason, nameof(reason), maxLength: 128);
    }

    public void DeferScheduling(DateTime dueUtc, string reason)
    {
        ReleaseScheduling(dueUtc, reason);
        State = NotificationDispatchState.Deferred;
        IsPreparationDeferred = true;
    }

    public void RetryPreparation(DateTime nowUtc)
    {
        if (State != NotificationDispatchState.NeedsReview || nowUtc.Kind != DateTimeKind.Utc ||
            OutcomeReason == "contradictory_delivery_evidence")
            throw new InvalidOperationException("Only an unlinked preparation review can be explicitly retried.");
        State = NotificationDispatchState.Pending;
        NextEvaluationAtUtc = nowUtc;
        OutcomeReason = null;
        IsPreparationDeferred = false;
    }

    public void Cancel()
    {
        if (State is not (NotificationDispatchState.Pending or NotificationDispatchState.Scheduling) && !IsPreparationDeferred)
            throw new InvalidOperationException("An activated channel cannot be retracted by cancelling its plan.");
        State = NotificationDispatchState.Cancelled;
        IsPreparationDeferred = false;
        SchedulingClaimId = null;
        ClaimExpiresAtUtc = null;
    }
}
