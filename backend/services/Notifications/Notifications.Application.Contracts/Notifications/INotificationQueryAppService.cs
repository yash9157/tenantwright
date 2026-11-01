using Notifications.Application.Contracts.Common;

namespace Notifications.Application.Contracts.Notifications;

public interface INotificationQueryAppService
{
    Task<PagedResultDto<NotificationListItemDto>> GetListAsync(GetNotificationsInput input);
    Task<NotificationDto> GetAsync(Guid id);
    Task<List<NotificationDeliveryDto>> GetDeliveriesAsync(Guid notificationId);
    Task<PagedResultDto<NotificationDeliveryChronologyItemDto>> GetDeliveryChronologyAsync(
        long deliveryId,
        GetNotificationDeliveryChronologyInput input,
        CancellationToken cancellationToken = default);
    Task<PagedResultDto<NotificationListItemDto>> GetCurrentTenantListAsync(GetNotificationsInput input);
    Task<NotificationDto> GetCurrentTenantAsync(Guid id);
    Task<List<NotificationDeliveryDto>> GetCurrentTenantDeliveriesAsync(Guid notificationId);
    Task<PagedResultDto<NotificationDeliveryChronologyItemDto>> GetCurrentTenantDeliveryChronologyAsync(
        long deliveryId,
        GetNotificationDeliveryChronologyInput input,
        CancellationToken cancellationToken = default);
    Task<List<NotificationRecoverySummaryDto>> GetCurrentTenantRecoveryAsync(
        CancellationToken cancellationToken = default);
    Task<int> CountPendingDeliveriesByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
