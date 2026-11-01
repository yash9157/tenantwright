using Notifications.Application.Contracts.Templates;
using Shared.Modularity.DependencyInjection;

using Shared.AuditLogs.Auditing;
namespace Notifications.Application.Templates;

[DisableConventionalRegistration]
public sealed class TemplateRendererAppService : ITemplateRendererAppService, ITransientDependency
{
    private readonly INotificationsTemplateRenderer _renderer;

    public TemplateRendererAppService(INotificationsTemplateRenderer renderer)
    {
        _renderer = renderer;
    }

    public async Task<RenderTemplateResultDto> RenderAsync(RenderTemplateRequestDto input, CancellationToken cancellationToken = default)
    {
        var rendered = await _renderer.RenderAsync(input.TemplateName, input.Model, cancellationToken);
        return new RenderTemplateResultDto
        {
            Content = rendered.Content,
            RenderedSubject = rendered.Subject
        };
    }

    public async Task<RenderTemplateResultDto> RenderDraftAsync(PreviewNotificationTemplateDraftInput input, CancellationToken cancellationToken = default)
    {
        var rendered = await _renderer.RenderDraftAsync(
            input.TemplateName,
            input.BodyTemplate,
            input.SubjectTemplate,
            input.Model,
            cancellationToken);

        return new RenderTemplateResultDto
        {
            Content = rendered.Content,
            RenderedSubject = rendered.Subject
        };
    }
}
