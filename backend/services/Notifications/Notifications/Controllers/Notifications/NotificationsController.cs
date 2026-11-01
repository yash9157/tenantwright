using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Notifications;
using Shared.Permissions.Definitions.Notifications;
using Shared.AuditLogs.Auditing;

namespace Notifications.Controllers.Notifications;

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationQueryAppService _notificationQueryAppService;
    private readonly INotificationDeliveryOperationsAppService _deliveryOperationsAppService;

    public NotificationsController(
        INotificationQueryAppService notificationQueryAppService,
        INotificationDeliveryOperationsAppService deliveryOperationsAppService)
    {
        _notificationQueryAppService = notificationQueryAppService;
        _deliveryOperationsAppService = deliveryOperationsAppService;
    }

    [HttpGet]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<PagedResultDto<NotificationListItemDto>> GetListAsync([FromQuery] GetNotificationsInput input)
    {
        return _notificationQueryAppService.GetListAsync(input);
    }

    [HttpGet("current-tenant")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<PagedResultDto<NotificationListItemDto>> GetCurrentTenantListAsync([FromQuery] GetNotificationsInput input)
    {
        return _notificationQueryAppService.GetCurrentTenantListAsync(input);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<NotificationDto> GetAsync(Guid id)
    {
        return _notificationQueryAppService.GetAsync(id);
    }

    [HttpGet("current-tenant/{id:guid}")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<NotificationDto> GetCurrentTenantAsync(Guid id)
    {
        return _notificationQueryAppService.GetCurrentTenantAsync(id);
    }

    [HttpGet("{id:guid}/deliveries")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<List<NotificationDeliveryDto>> GetDeliveriesAsync(Guid id)
    {
        return _notificationQueryAppService.GetDeliveriesAsync(id);
    }

    [HttpGet("current-tenant/{id:guid}/deliveries")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<List<NotificationDeliveryDto>> GetCurrentTenantDeliveriesAsync(Guid id)
    {
        return _notificationQueryAppService.GetCurrentTenantDeliveriesAsync(id);
    }

    [HttpGet("deliveries/{deliveryId:long}/chronology")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<PagedResultDto<NotificationDeliveryChronologyItemDto>> GetDeliveryChronologyAsync(
        long deliveryId,
        [FromQuery] GetNotificationDeliveryChronologyInput input,
        CancellationToken cancellationToken)
    {
        return _notificationQueryAppService.GetDeliveryChronologyAsync(
            deliveryId,
            input,
            cancellationToken);
    }

    [HttpGet("current-tenant/deliveries/{deliveryId:long}/chronology")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<PagedResultDto<NotificationDeliveryChronologyItemDto>> GetCurrentTenantDeliveryChronologyAsync(
        long deliveryId,
        [FromQuery] GetNotificationDeliveryChronologyInput input,
        CancellationToken cancellationToken)
    {
        return _notificationQueryAppService.GetCurrentTenantDeliveryChronologyAsync(
            deliveryId,
            input,
            cancellationToken);
    }

    [HttpGet("current-tenant/recovery")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<List<NotificationRecoverySummaryDto>> GetCurrentTenantRecoveryAsync(
        CancellationToken cancellationToken)
    {
        return _notificationQueryAppService.GetCurrentTenantRecoveryAsync(cancellationToken);
    }

    [HttpPost("{id:guid}/deliveries/resend-failed")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Deliveries.Resend)]
    public Task<ResendNotificationDeliveryResultDto> ResendFailedDeliveriesAsync(Guid id, CancellationToken cancellationToken)
    {
        return _deliveryOperationsAppService.ResendFailedAsync(
            new ResendFailedNotificationDeliveriesInput
            {
                NotificationId = id
            },
            cancellationToken);
    }

    [HttpPost("deliveries/{deliveryId:long}/resend")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Deliveries.Resend)]
    public Task<ResendNotificationDeliveryResultDto> ResendDeliveryAsync(long deliveryId, CancellationToken cancellationToken)
    {
        return _deliveryOperationsAppService.ResendDeliveryAsync(
            new ResendNotificationDeliveryInput
            {
                DeliveryId = deliveryId
            },
            cancellationToken);
    }

    [HttpPost("deliveries/{deliveryId:long}/override-preview")]
    [Authorize(Policy = NotificationsPermissions.Deliveries.OverrideResend)]
    public Task<NotificationDeliveryOverridePreviewDto> PreviewDeliveryOverrideResendAsync(
        long deliveryId,
        [FromBody] PreviewNotificationDeliveryOverrideResendInput input,
        CancellationToken cancellationToken)
    {
        input.DeliveryId = deliveryId;
        return _deliveryOperationsAppService.PreviewOverrideResendAsync(input, cancellationToken);
    }

    [HttpPost("deliveries/{deliveryId:long}/override-resend")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Deliveries.OverrideResend)]
    public Task<ResendNotificationDeliveryResultDto> ResendDeliveryOverrideAsync(
        long deliveryId,
        [FromBody] ResendNotificationDeliveryOverrideInput input,
        CancellationToken cancellationToken)
    {
        input.DeliveryId = deliveryId;
        return _deliveryOperationsAppService.ResendOverrideAsync(input, cancellationToken);
    }

    [HttpPost("deliveries/{deliveryId:long}/review/confirm")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Deliveries.Resend)]
    public Task<ReconcileNotificationDeliveryResultDto> ConfirmDeliveryAsync(
        long deliveryId,
        [FromBody] ConfirmNotificationDeliveryInput input,
        CancellationToken cancellationToken)
    {
        input.DeliveryId = deliveryId;
        return _deliveryOperationsAppService.ConfirmDeliveryAsync(input, cancellationToken);
    }

    [HttpPost("deliveries/{deliveryId:long}/review/retry")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Deliveries.Resend)]
    public Task<ReconcileNotificationDeliveryResultDto> RetryDeliveryAsync(
        long deliveryId,
        CancellationToken cancellationToken)
    {
        return _deliveryOperationsAppService.RetryDeliveryAsync(
            new RetryNotificationDeliveryInput
            {
                DeliveryId = deliveryId
            },
            cancellationToken);
    }
}
