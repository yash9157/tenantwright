using Microsoft.EntityFrameworkCore;
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

internal sealed partial class NotificationTemplateAdminAppService
{
    public async Task<NotificationTemplateManageDetailDto> GetManageAsync(string templateName, CancellationToken cancellationToken = default)
    {
        var template = await GetDefinitionAsync(templateName, cancellationToken);
        return await BuildManageDetailAsync(template, cancellationToken);
    }

    public async Task<NotificationTemplateManageDetailDto> CreateAsync(
        CreateNotificationTemplateInput input,
        CancellationToken cancellationToken = default)
    {
        await _importService.EnsureImportedAsync(cancellationToken);

        var displayName = NormalizeRequired(input.DisplayName, nameof(input.DisplayName), 256);
        var category = NormalizeOptional(input.Category, nameof(input.Category), 128) ?? "General";
        var templateName = BuildCanonicalTemplateName(category, displayName);
        if (string.Equals(templateName, _templateOptions.LayoutTemplateName, StringComparison.OrdinalIgnoreCase))
        {
            throw new SharedConflictException(
                NotificationErrorCodes.TemplateNameReserved,
                "The layout template key is reserved.");
        }

        var existingNames = await (await _definitionsRepository.GetQueryableAsync())
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);
        if (existingNames.Any(name => string.Equals(name, templateName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SharedConflictException(
                NotificationErrorCodes.TemplateNameExists,
                $"Notification template '{templateName}' already exists.");
        }

        var template = new NotificationTemplateDefinition(
            id: Guid.CreateVersion7(),
            name: templateName,
            displayName: displayName,
            category: category,
            relativePath: BuildDatabaseRelativePath(templateName),
            draftBodyTemplate: global::Shared.Check.NotNullOrWhiteSpace(input.BodyTemplate, nameof(input.BodyTemplate)),
            draftSubjectTemplate: NormalizeOptional(input.SubjectTemplate, nameof(input.SubjectTemplate), 2048));

        await _definitionsRepository.InsertAsync(template, autoSave: true, cancellationToken);
        return await BuildManageDetailAsync(template, cancellationToken);
    }

    public Task<RenderTemplateResultDto> PreviewDraftAsync(PreviewNotificationTemplateDraftInput input, CancellationToken cancellationToken = default)
    {
        return _templateRendererAppService.RenderDraftAsync(input, cancellationToken);
    }

    public Task<NotificationTemplateValidationResultDto> ValidateDraftAsync(
        ValidateNotificationTemplateDraftInput input,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var issues = new List<NotificationTemplateValidationIssueDto>();
        var variables = ExtractVariables(input.SubjectTemplate, input.BodyTemplate);

        ValidateTemplateText(NotificationTemplateValidationSources.Subject, input.SubjectTemplate, issues);
        if (string.IsNullOrWhiteSpace(input.BodyTemplate))
        {
            issues.Add(new NotificationTemplateValidationIssueDto
            {
                Severity = NotificationTemplateValidationIssueSeverities.Error,
                Code = NotificationTemplateValidationIssueCodes.BodyRequired,
                Source = NotificationTemplateValidationSources.Body,
                Message = "Draft body template is required before validation or publishing."
            });
        }
        else
        {
            ValidateTemplateText(NotificationTemplateValidationSources.Body, input.BodyTemplate, issues);
        }

        var model = input.Model ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        var missingModelKeys = variables
            .Select(variable => variable.ModelKey)
            .Where(key => !string.IsNullOrWhiteSpace(key) && !model.ContainsKey(key))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        foreach (var missingModelKey in missingModelKeys)
        {
            issues.Add(new NotificationTemplateValidationIssueDto
            {
                Severity = NotificationTemplateValidationIssueSeverities.Warning,
                Code = NotificationTemplateValidationIssueCodes.MissingSampleValue,
                Source = "Model",
                Message = $"Sample model is missing '{missingModelKey}'."
            });
        }

        var suggestedModel = new Dictionary<string, string?>(model, StringComparer.Ordinal);
        foreach (var missingModelKey in missingModelKeys)
        {
            suggestedModel[missingModelKey] = $"sample-{missingModelKey}";
        }

        var hasErrors = issues.Any(issue => issue.Severity == NotificationTemplateValidationIssueSeverities.Error);
        return Task.FromResult(new NotificationTemplateValidationResultDto
        {
            IsValid = !hasErrors,
            CanRender = !hasErrors,
            Variables = variables,
            MissingModelKeys = missingModelKeys,
            SuggestedModel = suggestedModel,
            Issues = issues
        });
    }

    public async Task<NotificationTemplateManageDetailDto> SaveDraftAsync(
        SaveNotificationTemplateDraftInput input,
        CancellationToken cancellationToken = default)
    {
        var template = await GetDefinitionAsync(input.TemplateName, cancellationToken);
        template.UpdateDraft(
            input.DisplayName,
            input.Category,
            input.BodyTemplate,
            input.SubjectTemplate);

        await _definitionsRepository.UpdateAsync(template, autoSave: true, cancellationToken);
        return await BuildManageDetailAsync(template, cancellationToken);
    }

    public async Task<NotificationTemplateManageDetailDto> PublishDraftAsync(
        PublishNotificationTemplateDraftInput input,
        CancellationToken cancellationToken = default)
    {
        var template = await GetDefinitionAsync(input.TemplateName, cancellationToken);
        var revision = await CreatePublishedRevisionAsync(template, cancellationToken);
        template.MarkPublished(revision, DateTime.UtcNow);

        await _definitionsRepository.UpdateAsync(template, autoSave: true, cancellationToken);
        return await BuildManageDetailAsync(template, cancellationToken);
    }

    public async Task<NotificationTemplateManageDetailDto> RenameAsync(
        RenameNotificationTemplateInput input,
        CancellationToken cancellationToken = default)
    {
        var template = await GetDefinitionAsync(input.TemplateName, cancellationToken);
        var revisionCount = await GetRevisionCountAsync(template.Id, cancellationToken);
        EnsureDraftOnlyDatabaseTemplate(template, revisionCount, "Only draft-only database-backed templates without revisions can be renamed.");

        var newDisplayName = NormalizeRequired(input.NewDisplayName, nameof(input.NewDisplayName), 256);
        var newTemplateName = BuildCanonicalTemplateName(template.Category, newDisplayName);
        if (string.Equals(newTemplateName, _templateOptions.LayoutTemplateName, StringComparison.OrdinalIgnoreCase))
        {
            throw new SharedConflictException(
                NotificationErrorCodes.TemplateNameReserved,
                "The layout template key is reserved.");
        }

        var normalizedTemplateName = newTemplateName.ToLower();
        var duplicate = await (await _definitionsRepository.GetQueryableAsync())
            .AnyAsync(x => x.Id != template.Id && x.Name.ToLower() == normalizedTemplateName, cancellationToken);
        if (duplicate)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.TemplateNameExists,
                $"Notification template '{newTemplateName}' already exists.");
        }

        template.UpdateDraft(
            newDisplayName,
            template.Category,
            template.DraftBodyTemplate,
            template.DraftSubjectTemplate);
        template.RenameDraftOnly(newTemplateName, BuildDatabaseRelativePath(newTemplateName));
        await _definitionsRepository.UpdateAsync(template, autoSave: true, cancellationToken);
        return await BuildManageDetailAsync(template, cancellationToken);
    }

    public async Task<NotificationTemplateManageDetailDto> ArchiveAsync(
        NotificationTemplateNameInput input,
        CancellationToken cancellationToken = default)
    {
        var template = await GetDefinitionAsync(input.TemplateName, cancellationToken);
        template.Archive(DateTime.UtcNow);

        await _definitionsRepository.UpdateAsync(template, autoSave: true, cancellationToken);
        return await BuildManageDetailAsync(template, cancellationToken);
    }

    public async Task<NotificationTemplateManageDetailDto> RestoreAsync(
        NotificationTemplateNameInput input,
        CancellationToken cancellationToken = default)
    {
        var template = await GetDefinitionAsync(input.TemplateName, cancellationToken);
        template.Restore();

        await _definitionsRepository.UpdateAsync(template, autoSave: true, cancellationToken);
        return await BuildManageDetailAsync(template, cancellationToken);
    }

    public async Task DeleteDraftAsync(NotificationTemplateNameInput input, CancellationToken cancellationToken = default)
    {
        var template = await GetDefinitionAsync(input.TemplateName, cancellationToken);
        var revisionCount = await GetRevisionCountAsync(template.Id, cancellationToken);
        EnsureDraftOnlyDatabaseTemplate(template, revisionCount, "Only draft-only database-backed templates without revisions can be deleted.");

        await _definitionsRepository.DeleteAsync(template, autoSave: true, cancellationToken);
    }

}
