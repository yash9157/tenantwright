using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Templates;
using Notifications.Domain.Templates;
using Notifications.Domain.Shared.Constants;
using Scriban;
using Shared;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notifications.Application.Templates;

[DisableConventionalRegistration]
internal sealed partial class NotificationTemplateAdminAppService : INotificationTemplateAdminAppService, ITransientDependency
{
    private readonly IRepository<NotificationTemplateDefinition, Guid> _definitionsRepository;
    private readonly IRepository<NotificationTemplateRevision, Guid> _revisionsRepository;
    private readonly IRepository<NotificationTemplateSample, Guid> _samplesRepository;
    private readonly IRepository<NotificationTemplateContentBlock, Guid> _contentBlocksRepository;
    private readonly NotificationTemplateImportService _importService;
    private readonly ITemplateRendererAppService _templateRendererAppService;
    private readonly NotificationsTemplateOptions _templateOptions;
    private static readonly JsonSerializerOptions SampleJsonOptions = new(JsonSerializerDefaults.Web);

    public NotificationTemplateAdminAppService(
        IRepository<NotificationTemplateDefinition, Guid> definitionsRepository,
        IRepository<NotificationTemplateRevision, Guid> revisionsRepository,
        IRepository<NotificationTemplateSample, Guid> samplesRepository,
        IRepository<NotificationTemplateContentBlock, Guid> contentBlocksRepository,
        NotificationTemplateImportService importService,
        ITemplateRendererAppService templateRendererAppService,
        IOptions<NotificationsTemplateOptions> templateOptions)
    {
        _definitionsRepository = definitionsRepository;
        _revisionsRepository = revisionsRepository;
        _samplesRepository = samplesRepository;
        _contentBlocksRepository = contentBlocksRepository;
        _importService = importService;
        _templateRendererAppService = templateRendererAppService;
        _templateOptions = templateOptions.Value;
    }

    private static List<NotificationTemplateDefinition> ApplyArchiveFilter(
        List<NotificationTemplateDefinition> templates,
        string? archiveState)
    {
        var normalized = archiveState?.Trim().ToLowerInvariant();
        return normalized switch
        {
            NotificationTemplateArchiveStates.All => templates,
            NotificationTemplateArchiveStates.Archived => templates.Where(template => template.IsArchived).ToList(),
            _ => templates.Where(template => !template.IsArchived).ToList()
        };
    }

    private static void EnsureDraftOnlyDatabaseTemplate(
        NotificationTemplateDefinition template,
        int revisionCount,
        string message)
    {
        if (!string.Equals(GetSourceType(template), "Database", StringComparison.OrdinalIgnoreCase) ||
            template.PublishedRevisionId.HasValue ||
            template.PublishedRevisionNumber.HasValue ||
            revisionCount > 0)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.TemplateNotDraftOnly,
                message);
        }
    }

    private static string BuildCanonicalTemplateName(string category, string displayName)
    {
        static string Canonicalize(string value, string fallback)
        {
            var words = Regex.Matches(value, "[A-Za-z0-9]+")
                .Select(match => match.Value)
                .Where(word => word.Length > 0)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..])
                .ToArray();
            var canonical = string.Concat(words);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = fallback;
            }

            return char.IsDigit(canonical[0]) ? fallback + canonical : canonical;
        }

        return $"{Canonicalize(category, "General")}.{Canonicalize(displayName, "Template")}";
    }

    private string BuildDatabaseRelativePath(string templateName)
    {
        var extension = _templateOptions.Extension.StartsWith('.')
            ? _templateOptions.Extension
            : "." + _templateOptions.Extension;
        return $"Database/{templateName.Replace('.', '/')}{extension}";
    }

    private static string SerializeSampleModel(Dictionary<string, string?>? model)
    {
        return JsonSerializer.Serialize(model ?? new Dictionary<string, string?>(StringComparer.Ordinal), SampleJsonOptions);
    }

    private static Dictionary<string, string?> DeserializeSampleModel(string modelJson)
    {
        if (string.IsNullOrWhiteSpace(modelJson))
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string?>>(modelJson, SampleJsonOptions) ??
               new Dictionary<string, string?>(StringComparer.Ordinal);
    }

    private static string NormalizeRequired(string value, string paramName, int maxLength)
    {
        return global::Shared.Check.NotNullOrWhiteSpace(value, paramName, maxLength: maxLength).Trim();
    }

    private static string? NormalizeOptional(string? value, string paramName, int maxLength)
    {
        var normalized = value?.Trim();
        if (normalized != null && normalized.Length > maxLength)
        {
            throw new ArgumentException($"{paramName} length must be equal to or lower than {maxLength}!", paramName);
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string GetSourceType(NotificationTemplateDefinition template)
    {
        return template.PublishedRevisionId.HasValue || template.RelativePath.StartsWith("Database/", StringComparison.OrdinalIgnoreCase)
            ? "Database"
            : "FileSystem";
    }

    private static void ValidateTemplateText(
        string source,
        string? templateText,
        List<NotificationTemplateValidationIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(templateText))
        {
            return;
        }

        var parsed = Template.Parse(templateText);
        if (!parsed.HasErrors)
        {
            return;
        }

        foreach (var message in parsed.Messages)
        {
            issues.Add(new NotificationTemplateValidationIssueDto
            {
                Severity = NotificationTemplateValidationIssueSeverities.Error,
                Code = NotificationTemplateValidationIssueCodes.TemplateSyntax,
                Source = source,
                Message = message.Message
            });
        }
    }

    private static List<NotificationTemplateVariableDto> ExtractVariables(string? subjectTemplate, string bodyTemplate)
    {
        var variables = new Dictionary<string, NotificationTemplateVariableDto>(StringComparer.Ordinal);
        AddVariables(NotificationTemplateValidationSources.Subject, subjectTemplate, variables);
        AddVariables(NotificationTemplateValidationSources.Body, bodyTemplate, variables);

        return variables.Values
            .OrderBy(variable => variable.Source, StringComparer.Ordinal)
            .ThenBy(variable => variable.ModelKey, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddVariables(
        string source,
        string? templateText,
        Dictionary<string, NotificationTemplateVariableDto> variables)
    {
        if (string.IsNullOrWhiteSpace(templateText))
        {
            return;
        }

        foreach (Match match in TemplateExpressionRegex().Matches(templateText))
        {
            var expression = match.Groups["expression"].Value.Trim();
            if (!TryGetModelKey(expression, out var modelKey))
            {
                continue;
            }

            var key = $"{source}:{expression}";
            if (variables.TryGetValue(key, out var existing))
            {
                existing.Occurrences++;
                continue;
            }

            variables[key] = new NotificationTemplateVariableDto
            {
                Source = source,
                Expression = expression,
                ModelKey = modelKey,
                Occurrences = 1
            };
        }
    }

    private static bool TryGetModelKey(string expression, out string modelKey)
    {
        modelKey = "";
        var normalized = expression.Trim();
        if (!normalized.StartsWith("model.", StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = normalized["model.".Length..];
        var end = remainder.IndexOfAny([' ', '|', '}', ')', ']', '[', '(']);
        modelKey = (end >= 0 ? remainder[..end] : remainder).Trim();
        return !string.IsNullOrWhiteSpace(modelKey);
    }

    [GeneratedRegex(@"\{\{\s*(?<expression>[^{}]+?)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateExpressionRegex();
}
