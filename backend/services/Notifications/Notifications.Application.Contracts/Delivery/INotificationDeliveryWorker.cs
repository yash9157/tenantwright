namespace Notifications.Application.Contracts.Delivery;

public interface INotificationDeliveryWorker
{
    Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default);
}
