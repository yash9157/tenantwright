namespace Notifications.Application.Contracts.Templates;

public sealed class RenderedNotificationTemplate
{
    public string Content { get; init; } = "";
    public string? Subject { get; init; }
}
