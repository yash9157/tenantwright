namespace Notifications.Application.Contracts.Templates;

public class RenderTemplateResultDto
{
    public string Content { get; set; } = "";
    public string? RenderedSubject { get; set; }
}
