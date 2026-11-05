using Shared.EventContracts;

namespace Notifications.Application.Contracts.Dispatches;

public interface INotificationDispatchScheduler
{
    Task<NotificationDispatchAdmissionDto> ScheduleAsync(NotificationSendRequestedTenantEto input, CancellationToken cancellationToken = default);
    Task<NotificationDispatchAdmissionDto> ScheduleAsync(NotificationSendRequestedHostEto input, CancellationToken cancellationToken = default);
}

public sealed record NotificationDispatchAdmissionDto(Guid DispatchId, Guid RequestId, bool IsDuplicate);
