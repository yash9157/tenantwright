namespace Notifications.Application.Templates;

public sealed class NotificationsTemplateOptions
{
    public string RootPath { get; set; } = "Templates/Files";
    public string Extension { get; set; } = ".tpl";
    public string LayoutTemplateName { get; set; } = "Layout";
}
