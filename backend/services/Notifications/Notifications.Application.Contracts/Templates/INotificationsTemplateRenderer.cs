namespace Notifications.Application.Contracts.Templates;

public interface INotificationsTemplateRenderer
{
    Task<RenderedNotificationTemplate> RenderAsync(string templateName, Dictionary<string, string?> model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a stored template. When <paramref name="applyLayout"/> is <c>false</c> the
    /// configured (email) layout wrapper is skipped, yielding content-only output suitable for
    /// non-email surfaces such as the in-app inbox.
    /// </summary>
    Task<RenderedNotificationTemplate> RenderAsync(string templateName, Dictionary<string, string?> model, bool applyLayout, CancellationToken cancellationToken = default);
    Task<RenderedNotificationTemplate> RenderDraftAsync(
        string templateName,
        string bodyTemplate,
        string? subjectTemplate,
        Dictionary<string, string?> model,
        CancellationToken cancellationToken = default);
}
