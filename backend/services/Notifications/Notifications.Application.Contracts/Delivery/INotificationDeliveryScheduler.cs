using Shared.EventContracts;

namespace Notifications.Application.Contracts.Delivery;

public interface INotificationDeliveryScheduler
{
    Task<NotificationScheduleResult> ScheduleAsync(
        NotificationChannelRequestTenant input,
        CancellationToken cancellationToken = default);

    Task<NotificationScheduleResult> ScheduleAsync(
        NotificationChannelRequestHost input,
        CancellationToken cancellationToken = default);

    Task<NotificationScheduleResult> ScheduleAtAsync(
        NotificationChannelRequestTenant input,
        DateTime notBeforeUtc,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This scheduler does not support delayed delivery.");

    Task<NotificationScheduleResult> ScheduleAtAsync(
        NotificationChannelRequestHost input,
        DateTime notBeforeUtc,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This scheduler does not support delayed delivery.");
}

public sealed class NotificationScheduleResult
{
    public bool Accepted { get; init; }
    public bool IsDuplicate { get; init; }
    public bool SkippedByPreferences { get; init; }
    public bool RequiresReview { get; init; }
    public Guid RequestId { get; init; }
    public Guid NotificationId { get; init; }
    public int QueuedCount { get; init; }
    public DateTime? ScheduledForUtc { get; init; }
}
