using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.AuditLogs.Auditing;

namespace Notifications.Domain.Notifications;

public class NotificationRecipient : Entity<long>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public Guid NotificationId { get; private set; }

    public virtual Notification Notification { get; private set; } = default!;

    public NotificationRecipientType RecipientType { get; private set; }

    public string RecipientValue { get; private set; } = default!;

    protected NotificationRecipient()
    {
    }

    public NotificationRecipient(
        long id,
        Guid? tenantId,
        Guid notificationId,
        NotificationRecipientType recipientType,
        string recipientValue)
        : base(id)
    {
        TenantId = tenantId;
        NotificationId = notificationId;
        RecipientType = recipientType;
        RecipientValue = global::Shared.Check.NotNullOrWhiteSpace(recipientValue, nameof(recipientValue), maxLength: 256);
    }
}
