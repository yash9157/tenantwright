namespace Notifications.Domain.Shared.Enums;

public enum NotificationDigestBucketStatus
{
    Open = 1,
    Processing = 2,
    Emitted = 3,
    ClosedEmpty = 4
}
