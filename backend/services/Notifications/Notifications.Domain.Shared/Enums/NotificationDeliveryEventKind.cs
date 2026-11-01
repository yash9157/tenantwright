namespace Notifications.Domain.Shared.Enums;

public enum NotificationDeliveryEventKind
{
    Queued = 1,
    Processing = 2,
    ProviderAccepted = 3,
    Failed = 4,
    NeedsReview = 5,
    DeadLettered = 6,
    Requeued = 7,
    ResendRequested = 8,
    OverrideResendRequested = 9,
    ProviderFeedback = 10,
    OperatorConfirmed = 11,
    ProviderDelivered = 12,
    QuietHoursHeld = 13,
    QuietHoursReleased = 14,
    Delivered = 15
}
