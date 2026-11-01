using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Replay;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Notifications;

namespace Notifications.Controllers.NotificationReplay;

[ApiController]
[Route("api/notifications/replay")]
[Authorize]
public class NotificationReplayController : ControllerBase
{
    private readonly INotificationReplayAppService _notificationReplayAppService;

    public NotificationReplayController(INotificationReplayAppService notificationReplayAppService)
    {
        _notificationReplayAppService = notificationReplayAppService;
    }

    [HttpPost]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Replay.Execute)]
    public Task<ReplayNotificationResultDto> ReplayAsync([FromBody] ReplayNotificationInput input, CancellationToken cancellationToken)
    {
        return _notificationReplayAppService.ReplayAsync(input, cancellationToken);
    }

    [HttpPost("override-preview")]
    [Authorize(Policy = NotificationsPermissions.Replay.OverridePreview)]
    public Task<ReplayNotificationOverridePreviewDto> PreviewOverrideAsync(
        [FromBody] PreviewReplayNotificationOverrideInput input,
        CancellationToken cancellationToken)
    {
        return _notificationReplayAppService.PreviewOverrideAsync(input, cancellationToken);
    }

    [HttpPost("override")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Replay.OverrideExecute)]
    public Task<ReplayNotificationResultDto> ReplayOverrideAsync(
        [FromBody] ReplayNotificationOverrideInput input,
        CancellationToken cancellationToken)
    {
        return _notificationReplayAppService.ReplayOverrideAsync(input, cancellationToken);
    }
}
