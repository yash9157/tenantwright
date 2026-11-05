namespace Notifications.Application.Contracts.Dispatches;

public interface INotificationDispatchCoordinator
{
    Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default);
}
