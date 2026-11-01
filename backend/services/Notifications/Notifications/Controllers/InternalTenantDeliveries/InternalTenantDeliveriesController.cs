using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Notifications;
using Shared.AuditLogs.Auditing;

namespace Notifications.Controllers.InternalTenantDeliveries;

[ApiController]
[IntegrationService]
[Route("api/notifications/internal/tenant-deliveries")]
[Authorize(Policy = "NotificationsInternal")]
public sealed class InternalTenantDeliveriesController : ControllerBase
{
    private readonly INotificationQueryAppService _notificationQueryAppService;

    public InternalTenantDeliveriesController(INotificationQueryAppService notificationQueryAppService)
    {
        _notificationQueryAppService = notificationQueryAppService;
    }

    [HttpGet("pending-count")]
    public async Task<ActionResult<TenantPendingDeliveryCountResponse>> GetPendingCountAsync([FromQuery] Guid tenantId)
    {
        var count = await _notificationQueryAppService.CountPendingDeliveriesByTenantAsync(tenantId);
        return Ok(new TenantPendingDeliveryCountResponse { TenantId = tenantId, PendingDeliveryCount = count });
    }
}

public sealed class TenantPendingDeliveryCountResponse
{
    public Guid TenantId { get; set; }
    public int PendingDeliveryCount { get; set; }
}
