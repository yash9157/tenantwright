using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Policy;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Notifications;

namespace Notifications.Controllers.Policy;

[ApiController]
[Route("api/notifications/policy")]
[Authorize]
public sealed class NotificationPolicyController : ControllerBase
{
    private readonly INotificationPolicyAppService _service;

    public NotificationPolicyController(INotificationPolicyAppService service)
    {
        _service = service;
    }

    [HttpGet("tenants/{tenantId:guid}")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Policy.Read)]
    public Task<NotificationPolicyDto> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken)
        => _service.GetTenantPolicyAsync(tenantId, cancellationToken);

    [HttpPut("tenants/{tenantId:guid}")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Policy.Manage)]
    public Task<NotificationPolicyDto> UpdateTenantAsync(
        Guid tenantId,
        [FromBody] UpdateNotificationPolicyInput input,
        CancellationToken cancellationToken)
        => _service.UpdateTenantPolicyAsync(tenantId, input, cancellationToken);

    [HttpPost("tenants/preview")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Policy.Read)]
    public Task<NotificationPolicyPreviewDto> PreviewTenantAsync(
        [FromBody] NotificationPolicyPreviewInput input,
        CancellationToken cancellationToken)
        => _service.PreviewTenantPolicyAsync(input, cancellationToken);

    [HttpGet("account/preference")]
    public Task<UserNotificationPreferenceDto> GetMyPreferenceAsync(CancellationToken cancellationToken)
        => _service.GetMyPreferenceAsync(cancellationToken);

    [HttpPut("account/preference")]
    [Audited]
    public Task<UserNotificationPreferenceDto> UpdateMyPreferenceAsync(
        [FromBody] UpdateUserNotificationPreferenceInput input,
        CancellationToken cancellationToken)
        => _service.UpdateMyPreferenceAsync(input, cancellationToken);
}
