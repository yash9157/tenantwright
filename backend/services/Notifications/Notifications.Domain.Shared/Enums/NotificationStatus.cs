namespace Notifications.Domain.Shared.Enums;

public enum NotificationStatus
{
    Queued = 1,
    ProviderAccepted = 2,
    Failed = 3,
    Processing = 4,
    NeedsReview = 5,
    DeadLettered = 6,
    Delivered = 7
}
