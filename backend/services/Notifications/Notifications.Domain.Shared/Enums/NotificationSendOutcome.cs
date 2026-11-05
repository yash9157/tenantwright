namespace Notifications.Domain.Shared.Enums;

public enum NotificationSendOutcome
{
    Accepted = 1,
    Delivered = 2,
    RetryableRejection = 3,
    PermanentRejection = 4,
    UnavailableBeforeSend = 5,
    Ambiguous = 6
}
