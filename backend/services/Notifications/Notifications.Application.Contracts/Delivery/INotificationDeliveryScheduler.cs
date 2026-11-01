using Shared.EventContracts;

namespace Notifications.Application.Contracts.Delivery;

public interface INotificationDeliveryScheduler
{
    Task<NotificationScheduleResult> ScheduleAsync(
        NotificationSendRequestedTenantEto input,
        CancellationToken cancellationToken = default);

    Task<NotificationScheduleResult> ScheduleAsync(
        NotificationSendRequestedHostEto input,
        CancellationToken cancellationToken = default);

    Task<NotificationScheduleResult> ScheduleAtAsync(
        NotificationSendRequestedTenantEto input,
        DateTime notBeforeUtc,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This scheduler does not support delayed delivery.");

    Task<NotificationScheduleResult> ScheduleAtAsync(
        NotificationSendRequestedHostEto input,
        DateTime notBeforeUtc,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This scheduler does not support delayed delivery.");
}

public sealed class NotificationScheduleResult
{
    public bool Accepted { get; init; }
    public bool IsDuplicate { get; init; }
    public bool SkippedByPreferences { get; init; }
    public Guid RequestId { get; init; }
    public Guid NotificationId { get; init; }
    public int QueuedCount { get; init; }
    public DateTime? ScheduledForUtc { get; init; }
}
