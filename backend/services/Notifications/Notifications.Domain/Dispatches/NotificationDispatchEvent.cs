using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Notifications.Domain.Dispatches;

public sealed class NotificationDispatchEvent : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid ScopeId { get; private set; }
    public Guid DispatchId { get; private set; }
    public long Revision { get; private set; }
    public NotificationDispatchEventKind Kind { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public Guid? RecipientId { get; private set; }
    public Guid? StepId { get; private set; }
    public string? Reason { get; private set; }
    public string? ActorId { get; private set; }

    private NotificationDispatchEvent() { }

    internal NotificationDispatchEvent(Guid id, Guid? tenantId, Guid dispatchId, long revision,
        NotificationDispatchEventKind kind, DateTime occurredAtUtc, Guid? recipientId, Guid? stepId,
        string? reason, string? actorId) : base(id)
    {
        if (occurredAtUtc.Kind != DateTimeKind.Utc || revision <= 0 || !Enum.IsDefined(kind))
            throw new ArgumentException("Invalid dispatch history event.");
        TenantId = tenantId;
        ScopeId = tenantId ?? Guid.Empty;
        DispatchId = dispatchId;
        Revision = revision;
        Kind = kind;
        OccurredAtUtc = occurredAtUtc;
        RecipientId = recipientId;
        StepId = stepId;
        Reason = reason is null ? null : global::Shared.Check.NotNullOrWhiteSpace(reason, nameof(reason), maxLength: 128);
        ActorId = actorId is null ? null : global::Shared.Check.NotNullOrWhiteSpace(actorId, nameof(actorId), maxLength: 128);
    }
}
