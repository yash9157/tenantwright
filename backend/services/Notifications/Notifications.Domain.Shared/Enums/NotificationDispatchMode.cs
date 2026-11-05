namespace Notifications.Domain.Shared.Enums;

public enum NotificationDispatchMode
{
    Parallel = 1,
    OrderedFallback = 2
}

public enum NotificationDispatchState
{
    Pending = 1,
    Scheduling = 2,
    Queued = 3,
    Deferred = 4,
    Accepted = 5,
    Delivered = 6,
    Rejected = 7,
    Suppressed = 8,
    NeedsReview = 9,
    Cancelled = 10,
    Expired = 11,
    PartiallyCompleted = 12
}

public enum NotificationDispatchEventKind
{
    Admitted = 1,
    StepActivated = 2,
    StepScheduled = 3,
    OutcomeChanged = 4,
    CancelRequested = 5,
    DeadlineReached = 6,
    RecoveryRequested = 7
}
