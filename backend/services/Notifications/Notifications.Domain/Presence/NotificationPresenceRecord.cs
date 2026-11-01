using Shared.Domain;
using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.Presence;

public class NotificationPresenceRecord : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public string UserId { get; private set; } = default!;

    public DateTime LastSeenAtUtc { get; private set; }

    protected NotificationPresenceRecord()
    {
    }

    public NotificationPresenceRecord(Guid id, Guid? tenantId, string userId, DateTime lastSeenAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        UserId = global::Shared.Check.NotNullOrWhiteSpace(userId, nameof(userId), maxLength: 128);
        LastSeenAtUtc = lastSeenAtUtc;
    }

    public void MarkSeen(DateTime lastSeenAtUtc)
    {
        LastSeenAtUtc = lastSeenAtUtc;
    }
}
