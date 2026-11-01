namespace Notifications.Application.Contracts.Templates;

public class RenderTemplateRequestDto
{
    public string TemplateName { get; set; } = "";
    public Dictionary<string, string?> Model { get; set; } = new(StringComparer.Ordinal);
}
