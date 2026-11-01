using Notifications.Application.Contracts.Common;

namespace Notifications.Application.Contracts.Templates;

public interface INotificationTemplateAdminAppService
{
    Task<PagedResultDto<NotificationTemplateListItemDto>> GetListAsync(GetNotificationTemplatesInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateDetailDto> GetAsync(string templateName, CancellationToken cancellationToken = default);
    Task<NotificationTemplateManageDetailDto> GetManageAsync(string templateName, CancellationToken cancellationToken = default);
    Task<NotificationTemplateManageDetailDto> CreateAsync(CreateNotificationTemplateInput input, CancellationToken cancellationToken = default);
    Task<RenderTemplateResultDto> PreviewAsync(RenderTemplateRequestDto input, CancellationToken cancellationToken = default);
    Task<RenderTemplateResultDto> PreviewDraftAsync(PreviewNotificationTemplateDraftInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateValidationResultDto> ValidateDraftAsync(ValidateNotificationTemplateDraftInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateManageDetailDto> SaveDraftAsync(SaveNotificationTemplateDraftInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateManageDetailDto> PublishDraftAsync(PublishNotificationTemplateDraftInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateManageDetailDto> RepublishAsync(RepublishNotificationTemplateRevisionInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateManageDetailDto> RenameAsync(RenameNotificationTemplateInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateManageDetailDto> ArchiveAsync(NotificationTemplateNameInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateManageDetailDto> RestoreAsync(NotificationTemplateNameInput input, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(NotificationTemplateNameInput input, CancellationToken cancellationToken = default);
    Task<List<NotificationTemplateSampleDto>> GetSamplesAsync(string templateName, CancellationToken cancellationToken = default);
    Task<NotificationTemplateSampleDto> CreateSampleAsync(CreateNotificationTemplateSampleInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateSampleDto> UpdateSampleAsync(UpdateNotificationTemplateSampleInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateSampleDto> SetDefaultSampleAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteSampleAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PagedResultDto<NotificationTemplateContentBlockDto>> GetContentBlocksAsync(GetNotificationTemplateContentBlocksInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateContentBlockDto> CreateContentBlockAsync(CreateNotificationTemplateContentBlockInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateContentBlockDto> UpdateContentBlockAsync(UpdateNotificationTemplateContentBlockInput input, CancellationToken cancellationToken = default);
    Task<NotificationTemplateContentBlockDto> ArchiveContentBlockAsync(Guid id, CancellationToken cancellationToken = default);
    Task<NotificationTemplateContentBlockDto> RestoreContentBlockAsync(Guid id, CancellationToken cancellationToken = default);
}
