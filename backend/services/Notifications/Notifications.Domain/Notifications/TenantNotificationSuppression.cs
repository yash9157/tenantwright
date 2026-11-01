namespace Notifications.Domain.Notifications;

public sealed class TenantNotificationSuppression
{
    public Guid TenantId { get; set; }
    public bool IsSuppressed { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
