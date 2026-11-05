using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Notifications.Domain.Dispatches;

public sealed class NotificationDispatchRecipient : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid ScopeId { get; private set; }
    public Guid DispatchId { get; private set; }
    public string RecipientKey { get; private set; } = string.Empty;
    public Guid? PreferenceUserId { get; private set; }
    public NotificationDispatchState State { get; private set; } = NotificationDispatchState.Pending;
    public ICollection<NotificationDispatchStep> Steps { get; private set; } = [];

    private NotificationDispatchRecipient() { }

    public NotificationDispatchRecipient(Guid id, Guid? tenantId, Guid dispatchId, string recipientKey, Guid? preferenceUserId)
        : base(id)
    {
        if (id == Guid.Empty || dispatchId == Guid.Empty || tenantId == Guid.Empty || preferenceUserId == Guid.Empty)
            throw new ArgumentException("Recipient identities must be valid.");
        TenantId = tenantId;
        ScopeId = tenantId ?? Guid.Empty;
        DispatchId = dispatchId;
        RecipientKey = global::Shared.Check.NotNullOrWhiteSpace(recipientKey, nameof(recipientKey), maxLength: 128);
        PreferenceUserId = preferenceUserId;
    }

    public void AddStep(NotificationDispatchStep step)
    {
        if (step.RecipientId != Id || step.DispatchId != DispatchId || step.ScopeId != ScopeId ||
            Steps.Any(existing => existing.Ordinal == step.Ordinal || existing.Channel == step.Channel))
            throw new InvalidOperationException("Channel steps must have matching ownership, a distinct channel and unique order.");
        Steps.Add(step);
    }

    public IReadOnlyList<NotificationDispatchStep> EligibleSteps(NotificationDispatchMode mode, DateTime nowUtc)
    {
        var ordered = Steps.OrderBy(step => step.Ordinal).ToArray();
        if (mode == NotificationDispatchMode.Parallel)
            return ordered.Where(step => step.IsDue(nowUtc)).ToArray();
        // Accepted, deferred, review and an active scheduling/send stage block every later fallback stage.
        foreach (var step in ordered)
        {
            if (step.State is NotificationDispatchState.Rejected or NotificationDispatchState.Suppressed)
                continue;
            return step.IsDue(nowUtc) ? [step] : [];
        }
        return [];
    }

    public void RefreshSummary(NotificationDispatchMode mode)
    {
        if (mode == NotificationDispatchMode.OrderedFallback)
        {
            var active = Steps.OrderBy(step => step.Ordinal)
                .FirstOrDefault(step => step.State is not (NotificationDispatchState.Rejected or NotificationDispatchState.Suppressed));
            State = active?.State ?? NotificationDispatchOutcomes.Aggregate(Steps.Select(step => step.State));
            return;
        }
        State = NotificationDispatchOutcomes.Aggregate(Steps.Select(step => step.State));
    }
}
