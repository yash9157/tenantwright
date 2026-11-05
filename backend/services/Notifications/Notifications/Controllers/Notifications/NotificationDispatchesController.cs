using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Dispatches;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Notifications;

namespace Notifications.Controllers.Notifications;

[ApiController]
[Route("api/notifications/dispatches")]
[Authorize]
public sealed class NotificationDispatchesController(INotificationDispatchAppService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<PagedResultDto<NotificationDispatchDto>> GetListAsync([FromQuery] GetNotificationDispatchesInput input,
        CancellationToken cancellationToken) => service.GetListAsync(input, false, cancellationToken);

    [HttpGet("current-tenant")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<PagedResultDto<NotificationDispatchDto>> GetCurrentTenantListAsync([FromQuery] GetNotificationDispatchesInput input,
        CancellationToken cancellationToken) => service.GetListAsync(input, true, cancellationToken);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<NotificationDispatchDto> GetAsync(Guid id, CancellationToken cancellationToken) => service.GetAsync(id, false, cancellationToken);

    [HttpGet("current-tenant/{id:guid}")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<NotificationDispatchDto> GetCurrentTenantAsync(Guid id, CancellationToken cancellationToken) => service.GetAsync(id, true, cancellationToken);

    [HttpGet("{id:guid}/recipients")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<PagedResultDto<NotificationDispatchRecipientDto>> GetRecipientsAsync(Guid id, [FromQuery] NotificationDispatchPageInput input,
        CancellationToken cancellationToken) => service.GetRecipientsAsync(id, input, false, cancellationToken);

    [HttpGet("current-tenant/{id:guid}/recipients")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<PagedResultDto<NotificationDispatchRecipientDto>> GetCurrentTenantRecipientsAsync(Guid id, [FromQuery] NotificationDispatchPageInput input,
        CancellationToken cancellationToken) => service.GetRecipientsAsync(id, input, true, cancellationToken);

    [HttpGet("{id:guid}/history")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<PagedResultDto<NotificationDispatchEventDto>> GetHistoryAsync(Guid id, [FromQuery] NotificationDispatchPageInput input,
        CancellationToken cancellationToken) => service.GetHistoryAsync(id, input, false, cancellationToken);

    [HttpGet("current-tenant/{id:guid}/history")]
    [Authorize(Policy = NotificationsPermissions.Query.Read)]
    public Task<PagedResultDto<NotificationDispatchEventDto>> GetCurrentTenantHistoryAsync(Guid id, [FromQuery] NotificationDispatchPageInput input,
        CancellationToken cancellationToken) => service.GetHistoryAsync(id, input, true, cancellationToken);

    [HttpPost("{id:guid}/cancel")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Dispatches.Cancel)]
    public Task<NotificationDispatchDto> CancelAsync(Guid id, CancellationToken cancellationToken) => service.CancelAsync(id, false, cancellationToken);

    [HttpPost("current-tenant/{id:guid}/cancel")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Dispatches.Cancel)]
    public Task<NotificationDispatchDto> CancelCurrentTenantAsync(Guid id, CancellationToken cancellationToken) => service.CancelAsync(id, true, cancellationToken);

    [HttpPost("{id:guid}/steps/{stepId:guid}/retry")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Deliveries.Resend)]
    public Task<NotificationDispatchDto> RetryStepAsync(Guid id, Guid stepId, [FromBody] RetryNotificationDispatchStepInput input,
        CancellationToken cancellationToken) => service.RetryStepAsync(id, stepId, input, false, cancellationToken);

    [HttpPost("current-tenant/{id:guid}/steps/{stepId:guid}/retry")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Deliveries.Resend)]
    public Task<NotificationDispatchDto> RetryCurrentTenantStepAsync(Guid id, Guid stepId, [FromBody] RetryNotificationDispatchStepInput input,
        CancellationToken cancellationToken) => service.RetryStepAsync(id, stepId, input, true, cancellationToken);
}
