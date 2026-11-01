namespace Notifications.Application.Contracts.Templates;

public sealed class StoredNotificationTemplateContent
{
    public string TemplateName { get; init; } = "";
    public string BodyTemplate { get; init; } = "";
    public string? SubjectTemplate { get; init; }
    public string RelativePath { get; init; } = "";
    public string SourceType { get; init; } = "FileSystem";
}
