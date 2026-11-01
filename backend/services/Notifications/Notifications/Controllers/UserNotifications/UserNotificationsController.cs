using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.UserNotifications;
using Shared.Permissions.Definitions.Notifications;
using Shared.AuditLogs.Auditing;

namespace Notifications.Controllers.UserNotifications;

[ApiController]
[Route("api/notifications/inbox")]
[Authorize]
public class UserNotificationsController : ControllerBase
{
    private readonly IUserNotificationAppService _userNotificationAppService;

    public UserNotificationsController(IUserNotificationAppService userNotificationAppService)
    {
        _userNotificationAppService = userNotificationAppService;
    }

    [HttpGet]
    [Authorize(Policy = NotificationsPermissions.Inbox.Read)]
    public Task<PagedResultDto<UserNotificationDto>> GetInboxAsync([FromQuery] GetUserNotificationsInput input)
    {
        return _userNotificationAppService.GetInboxAsync(input);
    }

    [HttpGet("{notificationId:guid}")]
    [Authorize(Policy = NotificationsPermissions.Inbox.Read)]
    public Task<UserNotificationDto> GetAsync(Guid notificationId)
    {
        return _userNotificationAppService.GetAsync(notificationId);
    }

    [HttpPost("read")]
    [Authorize(Policy = NotificationsPermissions.Inbox.MarkRead)]
    [Audited]
    public Task<NotificationMutationResultDto> MarkAsReadAsync([FromBody] MarkUserNotificationReadInput input)
    {
        return _userNotificationAppService.MarkAsReadAsync(input);
    }

    [HttpPost("read-all")]
    [Authorize(Policy = NotificationsPermissions.Inbox.MarkRead)]
    [Audited]
    public Task<MarkAllUserNotificationsReadResultDto> MarkAllAsReadAsync([FromBody] MarkAllUserNotificationsReadInput input)
    {
        return _userNotificationAppService.MarkAllAsReadAsync(input);
    }

    [HttpPost("archive")]
    [Authorize(Policy = NotificationsPermissions.Inbox.Archive)]
    [Audited]
    public Task<NotificationMutationResultDto> ArchiveAsync([FromBody] ArchiveUserNotificationInput input)
    {
        return _userNotificationAppService.ArchiveAsync(input);
    }

    [HttpPost("delete")]
    [Authorize(Policy = NotificationsPermissions.Inbox.Delete)]
    [Audited]
    public Task<NotificationMutationResultDto> DeleteAsync([FromBody] DeleteUserNotificationInput input)
    {
        return _userNotificationAppService.DeleteAsync(input);
    }
}
