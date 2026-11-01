using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Templates;
using Notifications.Application.Templates;
using Xunit;

namespace Notifications.Tests;

public class TemplateRendererTests
{
    private sealed class InMemoryTemplateStore : ITemplateStore
    {
        private readonly Dictionary<string, StoredNotificationTemplateContent> _templates;

        public InMemoryTemplateStore(Dictionary<string, StoredNotificationTemplateContent> templates)
        {
            _templates = templates;
        }

        public Task<StoredNotificationTemplateContent?> GetAsync(string templateName, CancellationToken cancellationToken = default)
        {
            _templates.TryGetValue(templateName, out var template);
            return Task.FromResult(template);
        }
    }

    [Fact]
    public async Task RenderAsync_applies_layout_and_renders_model()
    {
        var store = new InMemoryTemplateStore(new Dictionary<string, StoredNotificationTemplateContent>(StringComparer.Ordinal)
        {
            ["Layout"] = new()
            {
                TemplateName = "Layout",
                BodyTemplate = "<html>{{model.content}}</html>",
                SourceType = "FileSystem"
            },
            ["Identity.PasswordResetLink"] = new()
            {
                TemplateName = "Identity.PasswordResetLink",
                BodyTemplate = "Hello {{model.name}}",
                SourceType = "Database"
            }
        });

        var options = Options.Create(new NotificationsTemplateOptions
        {
            LayoutTemplateName = "Layout"
        });

        var renderer = new ScribanNotificationsTemplateRenderer(store, options);

        var result = await renderer.RenderAsync(
            "Identity.PasswordResetLink",
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["name"] = "Ali" },
            CancellationToken.None);

        Assert.Equal("<html>Hello Ali</html>", result.Content);
        Assert.Null(result.Subject);
    }

    [Fact]
    public async Task RenderDraftAsync_should_render_subject_and_body()
    {
        var store = new InMemoryTemplateStore(new Dictionary<string, StoredNotificationTemplateContent>(StringComparer.Ordinal)
        {
            ["Layout"] = new()
            {
                TemplateName = "Layout",
                BodyTemplate = "<html>{{model.content}}</html>",
                SourceType = "FileSystem"
            }
        });

        var options = Options.Create(new NotificationsTemplateOptions
        {
            LayoutTemplateName = "Layout"
        });

        var renderer = new ScribanNotificationsTemplateRenderer(store, options);

        var result = await renderer.RenderDraftAsync(
            "Identity.PasswordResetLink",
            "Hello {{model.name}}",
            "Subject for {{model.name}}",
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["name"] = "Ali" },
            CancellationToken.None);

        Assert.Equal("<html>Hello Ali</html>", result.Content);
        Assert.Equal("Subject for Ali", result.Subject);
    }
}
