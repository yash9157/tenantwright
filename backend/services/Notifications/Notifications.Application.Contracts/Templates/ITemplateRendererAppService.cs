namespace Notifications.Application.Contracts.Templates;

public interface ITemplateRendererAppService
{
    Task<RenderTemplateResultDto> RenderAsync(RenderTemplateRequestDto input, CancellationToken cancellationToken = default);
    Task<RenderTemplateResultDto> RenderDraftAsync(PreviewNotificationTemplateDraftInput input, CancellationToken cancellationToken = default);
}
