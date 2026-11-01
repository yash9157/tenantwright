using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Templates;
using Shared.Permissions.Definitions.Notifications;
using Shared.AuditLogs.Auditing;

namespace Notifications.Controllers.NotificationTemplates;

[ApiController]
[Route("api/notifications/templates")]
[Authorize]
public class NotificationTemplatesController : ControllerBase
{
    private readonly INotificationTemplateAdminAppService _notificationTemplateAdminAppService;

    public NotificationTemplatesController(INotificationTemplateAdminAppService notificationTemplateAdminAppService)
    {
        _notificationTemplateAdminAppService = notificationTemplateAdminAppService;
    }

    [HttpGet]
    [Authorize(Policy = NotificationsPermissions.Templates.Read)]
    public Task<PagedResultDto<NotificationTemplateListItemDto>> GetListAsync([FromQuery] GetNotificationTemplatesInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.GetListAsync(input, cancellationToken);
    }

    [HttpGet("{templateName}")]
    [Authorize(Policy = NotificationsPermissions.Templates.Read)]
    public Task<NotificationTemplateDetailDto> GetAsync(string templateName, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.GetAsync(templateName, cancellationToken);
    }

    [HttpPost]
    [Authorize(Policy = NotificationsPermissions.Templates.Create)]
    [Audited]
    public Task<NotificationTemplateManageDetailDto> CreateAsync([FromBody] CreateNotificationTemplateInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.CreateAsync(input, cancellationToken);
    }

    [HttpGet("{templateName}/manage")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    public Task<NotificationTemplateManageDetailDto> GetManageAsync(string templateName, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.GetManageAsync(templateName, cancellationToken);
    }

    [HttpPost("preview")]
    [Authorize(Policy = NotificationsPermissions.Templates.Preview)]
    public Task<RenderTemplateResultDto> PreviewAsync([FromBody] RenderTemplateRequestDto input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.PreviewAsync(input, cancellationToken);
    }

    [HttpPost("preview-draft")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    public Task<RenderTemplateResultDto> PreviewDraftAsync([FromBody] PreviewNotificationTemplateDraftInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.PreviewDraftAsync(input, cancellationToken);
    }

    [HttpPost("validate-draft")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    public Task<NotificationTemplateValidationResultDto> ValidateDraftAsync([FromBody] ValidateNotificationTemplateDraftInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.ValidateDraftAsync(input, cancellationToken);
    }

    [HttpPut("draft")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    [Audited]
    public Task<NotificationTemplateManageDetailDto> SaveDraftAsync([FromBody] SaveNotificationTemplateDraftInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.SaveDraftAsync(input, cancellationToken);
    }

    [HttpPost("publish")]
    [Authorize(Policy = NotificationsPermissions.Templates.Publish)]
    [Audited]
    public Task<NotificationTemplateManageDetailDto> PublishDraftAsync([FromBody] PublishNotificationTemplateDraftInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.PublishDraftAsync(input, cancellationToken);
    }

    [HttpPost("republish")]
    [Authorize(Policy = NotificationsPermissions.Templates.Publish)]
    [Audited]
    public Task<NotificationTemplateManageDetailDto> RepublishAsync([FromBody] RepublishNotificationTemplateRevisionInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.RepublishAsync(input, cancellationToken);
    }

    [HttpPost("rename")]
    [Authorize(Policy = NotificationsPermissions.Templates.Rename)]
    [Audited]
    public Task<NotificationTemplateManageDetailDto> RenameAsync([FromBody] RenameNotificationTemplateInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.RenameAsync(input, cancellationToken);
    }

    [HttpPost("archive")]
    [Authorize(Policy = NotificationsPermissions.Templates.Archive)]
    [Audited]
    public Task<NotificationTemplateManageDetailDto> ArchiveAsync([FromBody] NotificationTemplateNameInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.ArchiveAsync(input, cancellationToken);
    }

    [HttpPost("restore")]
    [Authorize(Policy = NotificationsPermissions.Templates.Archive)]
    [Audited]
    public Task<NotificationTemplateManageDetailDto> RestoreAsync([FromBody] NotificationTemplateNameInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.RestoreAsync(input, cancellationToken);
    }

    [HttpPost("delete-draft")]
    [Authorize(Policy = NotificationsPermissions.Templates.DeleteDraft)]
    [Audited]
    public Task DeleteDraftAsync([FromBody] NotificationTemplateNameInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.DeleteDraftAsync(input, cancellationToken);
    }

    [HttpGet("{templateName}/samples")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    public Task<List<NotificationTemplateSampleDto>> GetSamplesAsync(string templateName, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.GetSamplesAsync(templateName, cancellationToken);
    }

    [HttpPost("samples")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    [Audited]
    public Task<NotificationTemplateSampleDto> CreateSampleAsync([FromBody] CreateNotificationTemplateSampleInput input, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.CreateSampleAsync(input, cancellationToken);
    }

    [HttpPut("samples/{id:guid}")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    [Audited]
    public Task<NotificationTemplateSampleDto> UpdateSampleAsync(Guid id, [FromBody] UpdateNotificationTemplateSampleInput input, CancellationToken cancellationToken)
    {
        input.Id = id;
        return _notificationTemplateAdminAppService.UpdateSampleAsync(input, cancellationToken);
    }

    [HttpPost("samples/{id:guid}/default")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    [Audited]
    public Task<NotificationTemplateSampleDto> SetDefaultSampleAsync(Guid id, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.SetDefaultSampleAsync(id, cancellationToken);
    }

    [HttpDelete("samples/{id:guid}")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    [Audited]
    public Task DeleteSampleAsync(Guid id, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.DeleteSampleAsync(id, cancellationToken);
    }

    [HttpGet("content-blocks")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    public Task<PagedResultDto<NotificationTemplateContentBlockDto>> GetContentBlocksAsync(
        [FromQuery] GetNotificationTemplateContentBlocksInput input,
        CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.GetContentBlocksAsync(input, cancellationToken);
    }

    [HttpPost("content-blocks")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    [Audited]
    public Task<NotificationTemplateContentBlockDto> CreateContentBlockAsync(
        [FromBody] CreateNotificationTemplateContentBlockInput input,
        CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.CreateContentBlockAsync(input, cancellationToken);
    }

    [HttpPut("content-blocks/{id:guid}")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    [Audited]
    public Task<NotificationTemplateContentBlockDto> UpdateContentBlockAsync(
        Guid id,
        [FromBody] UpdateNotificationTemplateContentBlockInput input,
        CancellationToken cancellationToken)
    {
        input.Id = id;
        return _notificationTemplateAdminAppService.UpdateContentBlockAsync(input, cancellationToken);
    }

    [HttpPost("content-blocks/{id:guid}/archive")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    [Audited]
    public Task<NotificationTemplateContentBlockDto> ArchiveContentBlockAsync(Guid id, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.ArchiveContentBlockAsync(id, cancellationToken);
    }

    [HttpPost("content-blocks/{id:guid}/restore")]
    [Authorize(Policy = NotificationsPermissions.Templates.Manage)]
    [Audited]
    public Task<NotificationTemplateContentBlockDto> RestoreContentBlockAsync(Guid id, CancellationToken cancellationToken)
    {
        return _notificationTemplateAdminAppService.RestoreContentBlockAsync(id, cancellationToken);
    }
}
