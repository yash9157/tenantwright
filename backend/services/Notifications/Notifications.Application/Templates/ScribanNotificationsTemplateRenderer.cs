using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Templates;
using Scriban;
using Scriban.Runtime;
using Shared.Modularity.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace Notifications.Application.Templates;

public sealed class ScribanNotificationsTemplateRenderer : INotificationsTemplateRenderer, ITransientDependency
{
    private static readonly ConcurrentDictionary<string, Template> ParsedTemplates = new(StringComparer.Ordinal);
    private static readonly Meter DeliveryMeter = new("Notifications.Delivery");
    private static readonly Histogram<double> TemplateRenderDurationMs =
        DeliveryMeter.CreateHistogram<double>("notifications.template.render.duration.ms");

    private readonly ITemplateStore _templateStore;
    private readonly IOptions<NotificationsTemplateOptions> _options;

    public ScribanNotificationsTemplateRenderer(
        ITemplateStore templateStore,
        IOptions<NotificationsTemplateOptions> options)
    {
        _templateStore = templateStore;
        _options = options;
    }

    public Task<RenderedNotificationTemplate> RenderAsync(string templateName, Dictionary<string, string?> model, CancellationToken cancellationToken = default)
        => RenderAsync(templateName, model, applyLayout: true, cancellationToken);

    public async Task<RenderedNotificationTemplate> RenderAsync(string templateName, Dictionary<string, string?> model, bool applyLayout, CancellationToken cancellationToken = default)
    {
        var content = await _templateStore.GetAsync(templateName, cancellationToken);
        if (content == null)
        {
            throw new InvalidOperationException($"Template not found: {templateName}");
        }

        return await RenderStoredAsync(content, model, applyLayout, cancellationToken);
    }

    public async Task<RenderedNotificationTemplate> RenderDraftAsync(
        string templateName,
        string bodyTemplate,
        string? subjectTemplate,
        Dictionary<string, string?> model,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(templateName))
        {
            throw new InvalidOperationException("Template name is required.");
        }

        return await RenderStoredAsync(
            new StoredNotificationTemplateContent
            {
                TemplateName = templateName.Trim(),
                BodyTemplate = bodyTemplate,
                SubjectTemplate = subjectTemplate,
                SourceType = "Draft"
            },
            model,
            applyLayout: true,
            cancellationToken);
    }

    private async Task<RenderedNotificationTemplate> RenderStoredAsync(
        StoredNotificationTemplateContent content,
        Dictionary<string, string?> model,
        bool applyLayout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var body = RenderRaw(content.TemplateName, content.BodyTemplate, model);
        string? renderedSubject = null;
        if (!string.IsNullOrWhiteSpace(content.SubjectTemplate))
        {
            renderedSubject = RenderRaw(content.TemplateName + ".subject", content.SubjectTemplate, model);
        }

        var layoutName = _options.Value.LayoutTemplateName;
        if (applyLayout && !string.IsNullOrWhiteSpace(layoutName) && !string.Equals(content.TemplateName, layoutName, StringComparison.Ordinal))
        {
            var layoutTemplate = await _templateStore.GetAsync(layoutName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(layoutTemplate?.BodyTemplate))
            {
                var layoutModel = new Dictionary<string, string?>(model, StringComparer.Ordinal)
                {
                    ["content"] = body
                };
                body = RenderRaw(layoutName, layoutTemplate.BodyTemplate, layoutModel);
            }
        }

        TemplateRenderDurationMs.Record(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new KeyValuePair<string, object?>("template", content.TemplateName));
        return new RenderedNotificationTemplate
        {
            Content = body,
            Subject = string.IsNullOrWhiteSpace(renderedSubject) ? null : renderedSubject
        };
    }

    private static string RenderRaw(string templateName, string templateText, Dictionary<string, string?> model)
    {
        var template = GetOrParseTemplate(templateName, templateText);

        var globals = new ScriptObject();
        globals.SetValue("model", model, true);

        var context = new TemplateContext();
        context.PushGlobal(globals);

        return template.Render(context);
    }

    private static Template GetOrParseTemplate(string templateName, string templateText)
    {
        var templateKey = $"{templateName}:{ComputeHash(templateText)}";

        return ParsedTemplates.GetOrAdd(templateKey, _ =>
        {
            var parsed = Template.Parse(templateText);
            if (parsed.HasErrors)
            {
                throw new InvalidOperationException("Template parse failed: " + string.Join("; ", parsed.Messages.Select(m => m.Message)));
            }

            return parsed;
        });
    }

    private static string ComputeHash(string templateText)
    {
        var bytes = Encoding.UTF8.GetBytes(templateText);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
