using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Presence;
using Shared.MultiTenancy;
using Shared.Permissions.Definitions.Notifications;
using Shared.AuditLogs.Auditing;

namespace Notifications.Controllers.Presence;

[ApiController]
[Route("api/notifications/presence")]
[Authorize(Policy = NotificationsPermissions.Presence.Read)]
public class PresenceController : ControllerBase
{
    private readonly INotificationsPresenceStore _presenceStore;
    private readonly INotificationPresenceQueryAppService _presenceQueryAppService;
    private readonly ICurrentTenant _currentTenant;

    public PresenceController(
        INotificationsPresenceStore presenceStore,
        INotificationPresenceQueryAppService presenceQueryAppService,
        ICurrentTenant currentTenant)
    {
        _presenceStore = presenceStore;
        _presenceQueryAppService = presenceQueryAppService;
        _currentTenant = currentTenant;
    }

    [HttpGet]
    public Task<bool> IsActiveAsync([FromQuery] string userId, [FromQuery] Guid? tenantId)
    {
        tenantId ??= _currentTenant.Id;
        if (_currentTenant.Id.HasValue && tenantId != _currentTenant.Id)
        {
            throw new UnauthorizedAccessException("Cross-tenant presence checks are not allowed.");
        }

        return _presenceStore.IsActiveAsync(tenantId, userId, HttpContext.RequestAborted);
    }

    [HttpGet("summary")]
    public Task<NotificationPresenceSummaryDto> GetSummaryAsync(
        [FromQuery] GetNotificationPresenceInput input,
        CancellationToken cancellationToken)
    {
        return _presenceQueryAppService.GetSummaryAsync(input, cancellationToken);
    }

    [HttpGet("observations")]
    public Task<PagedResultDto<NotificationPresenceItemDto>> GetObservationsAsync(
        [FromQuery] GetNotificationPresenceInput input,
        CancellationToken cancellationToken)
    {
        return _presenceQueryAppService.GetListAsync(input, cancellationToken);
    }
}
