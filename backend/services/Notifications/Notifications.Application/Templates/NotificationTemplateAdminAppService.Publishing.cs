using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Templates;
using Notifications.Domain.Templates;
using Notifications.Domain.Shared.Constants;
using Scriban;
using Shared;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notifications.Application.Templates;

internal sealed partial class NotificationTemplateAdminAppService
{
    public async Task<NotificationTemplateManageDetailDto> RepublishAsync(
        RepublishNotificationTemplateRevisionInput input,
        CancellationToken cancellationToken = default)
    {
        var template = await GetDefinitionAsync(input.TemplateName, cancellationToken);
        var revision = await (await _revisionsRepository.GetQueryableAsync())
            .FirstOrDefaultAsync(x => x.Id == input.RevisionId && x.TemplateId == template.Id, cancellationToken)
            ?? throw new EntityNotFoundException(
                NotificationErrorCodes.TemplateRevisionNotFound,
                "Template revision was not found.");

        template.UpdateDraft(
            revision.DisplayName,
            revision.Category,
            revision.BodyTemplate,
            revision.SubjectTemplate);
        await _definitionsRepository.UpdateAsync(template, autoSave: true, cancellationToken);

        var republished = await CreatePublishedRevisionAsync(template, cancellationToken);
        template.MarkPublished(republished, DateTime.UtcNow);
        await _definitionsRepository.UpdateAsync(template, autoSave: true, cancellationToken);
        return await BuildManageDetailAsync(template, cancellationToken);
    }

    private async Task<NotificationTemplateDefinition> GetDefinitionAsync(string templateName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(templateName))
        {
            throw new SharedValidationException(
                NotificationErrorCodes.TemplateNameRequired,
                "Template name is required.");
        }

        await _importService.EnsureImportedAsync(cancellationToken);
        return await (await _definitionsRepository.GetQueryableAsync())
            .FirstOrDefaultAsync(x => x.Name == templateName.Trim(), cancellationToken)
            ?? throw new EntityNotFoundException(
                NotificationErrorCodes.TemplateNotFound,
                $"Notification template '{templateName}' was not found.");
    }

    private async Task<NotificationTemplateDefinition> GetDefinitionByIdAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await _importService.EnsureImportedAsync(cancellationToken);
        return await (await _definitionsRepository.GetQueryableAsync())
            .FirstOrDefaultAsync(x => x.Id == templateId, cancellationToken)
            ?? throw new EntityNotFoundException(
                NotificationErrorCodes.TemplateNotFound,
                "Notification template was not found.");
    }

    private async Task<NotificationTemplateSample> GetSampleAsync(Guid id, CancellationToken cancellationToken)
    {
        return await (await _samplesRepository.GetQueryableAsync())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new EntityNotFoundException(
                NotificationErrorCodes.TemplateSampleNotFound,
                "Notification template sample was not found.");
    }

    private async Task EnsureUniqueSampleNameAsync(
        Guid templateId,
        string name,
        Guid? excludedSampleId,
        CancellationToken cancellationToken)
    {
        var normalizedName = name.Trim().ToLower();
        var duplicate = await (await _samplesRepository.GetQueryableAsync())
            .AnyAsync(
                sample =>
                    sample.TemplateId == templateId &&
                    (!excludedSampleId.HasValue || sample.Id != excludedSampleId.Value) &&
                    sample.Name.ToLower() == normalizedName,
                cancellationToken);

        if (duplicate)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.TemplateSampleNameExists,
                $"Notification template sample '{name}' already exists.");
        }
    }

    private async Task ClearDefaultSamplesAsync(Guid templateId, Guid? excludedSampleId, CancellationToken cancellationToken)
    {
        var defaults = await (await _samplesRepository.GetQueryableAsync())
            .Where(sample =>
                sample.TemplateId == templateId &&
                sample.IsDefault &&
                (!excludedSampleId.HasValue || sample.Id != excludedSampleId.Value))
            .ToListAsync(cancellationToken);

        foreach (var defaultSample in defaults)
        {
            defaultSample.ClearDefault();
            await _samplesRepository.UpdateAsync(defaultSample, autoSave: true, cancellationToken);
        }
    }

    private static NotificationTemplateSampleDto MapSample(
        NotificationTemplateDefinition template,
        NotificationTemplateSample sample)
    {
        return new NotificationTemplateSampleDto
        {
            Id = sample.Id,
            TemplateName = template.Name,
            Name = sample.Name,
            Description = sample.Description,
            Model = DeserializeSampleModel(sample.ModelJson),
            IsDefault = sample.IsDefault,
            CreatedAtUtc = sample.CreationTime,
            UpdatedAtUtc = sample.LastModificationTime
        };
    }

    private async Task<NotificationTemplateContentBlock> GetContentBlockAsync(Guid id, CancellationToken cancellationToken)
    {
        return await (await _contentBlocksRepository.GetQueryableAsync())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new EntityNotFoundException(
                NotificationErrorCodes.TemplateContentBlockNotFound,
                "Notification template content block was not found.");
    }

    private async Task EnsureUniqueContentBlockKeyAsync(
        string key,
        Guid? excludedBlockId,
        CancellationToken cancellationToken)
    {
        var normalizedKey = key.Trim().ToLower();
        var duplicate = await (await _contentBlocksRepository.GetQueryableAsync())
            .AnyAsync(
                block =>
                    (!excludedBlockId.HasValue || block.Id != excludedBlockId.Value) &&
                    block.Key.ToLower() == normalizedKey,
                cancellationToken);

        if (duplicate)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.TemplateContentBlockKeyExists,
                $"Notification template content block '{key}' already exists.");
        }
    }

    private static List<NotificationTemplateContentBlock> ApplyContentBlockArchiveFilter(
        List<NotificationTemplateContentBlock> blocks,
        string? archiveState)
    {
        var normalized = archiveState?.Trim().ToLowerInvariant();
        return normalized switch
        {
            NotificationTemplateArchiveStates.All => blocks,
            NotificationTemplateArchiveStates.Archived => blocks.Where(block => block.IsArchived).ToList(),
            _ => blocks.Where(block => !block.IsArchived).ToList()
        };
    }

    private static NotificationTemplateContentBlockDto MapContentBlock(NotificationTemplateContentBlock block)
    {
        return new NotificationTemplateContentBlockDto
        {
            Id = block.Id,
            Key = block.Key,
            DisplayName = block.DisplayName,
            Category = block.Category,
            Description = block.Description,
            SubjectSnippet = block.SubjectSnippet,
            BodySnippet = block.BodySnippet,
            IsArchived = block.IsArchived,
            ArchivedAtUtc = block.ArchivedAtUtc,
            ArchivedByUserId = block.ArchivedByUserId,
            CreatedAtUtc = block.CreationTime,
            UpdatedAtUtc = block.LastModificationTime
        };
    }

    private async Task<NotificationTemplateManageDetailDto> BuildManageDetailAsync(
        NotificationTemplateDefinition template,
        CancellationToken cancellationToken)
    {
        var revisions = await (await _revisionsRepository.GetQueryableAsync())
            .Where(x => x.TemplateId == template.Id)
            .OrderByDescending(x => x.RevisionNumber)
            .ToListAsync(cancellationToken);

        var publishedRevision = template.PublishedRevisionId.HasValue
            ? revisions.FirstOrDefault(x => x.Id == template.PublishedRevisionId.Value)
            : null;

        return new NotificationTemplateManageDetailDto
        {
            Name = template.Name,
            DisplayName = template.DisplayName,
            Category = template.Category,
            RelativePath = template.RelativePath,
            SourceType = GetSourceType(template),
            SupportsPreview = true,
            DraftSubjectTemplate = template.DraftSubjectTemplate,
            DraftBodyTemplate = template.DraftBodyTemplate,
            RevisionCount = revisions.Count,
            PublishedRevisionNumber = template.PublishedRevisionNumber,
            PublishedAtUtc = template.PublishedAtUtc,
            DraftUpdatedAtUtc = template.LastModificationTime ?? template.CreationTime,
            IsArchived = template.IsArchived,
            ArchivedAtUtc = template.ArchivedAtUtc,
            ArchivedByUserId = template.ArchivedByUserId,
            PublishedDisplayName = publishedRevision?.DisplayName,
            PublishedCategory = publishedRevision?.Category,
            PublishedSubjectTemplate = publishedRevision?.SubjectTemplate,
            PublishedBodyTemplate = publishedRevision?.BodyTemplate,
            Revisions = revisions.Select(revision => new NotificationTemplateRevisionListItemDto
            {
                Id = revision.Id,
                RevisionNumber = revision.RevisionNumber,
                DisplayName = revision.DisplayName,
                Category = revision.Category,
                CreatedAtUtc = revision.CreationTime,
                CreatorId = revision.CreatorId,
                IsPublished = template.PublishedRevisionId == revision.Id
            }).ToList()
        };
    }

    private async Task<NotificationTemplateRevision> CreatePublishedRevisionAsync(
        NotificationTemplateDefinition template,
        CancellationToken cancellationToken)
    {
        var revisionNumbers = await (await _revisionsRepository.GetQueryableAsync())
            .Where(x => x.TemplateId == template.Id)
            .Select(x => x.RevisionNumber)
            .ToListAsync(cancellationToken);
        var nextRevisionNumber = (revisionNumbers.Count == 0 ? 0 : revisionNumbers.Max()) + 1;

        var revision = new NotificationTemplateRevision(
            id: Guid.CreateVersion7(),
            templateId: template.Id,
            revisionNumber: nextRevisionNumber,
            displayName: template.DisplayName,
            category: template.Category,
            bodyTemplate: template.DraftBodyTemplate,
            subjectTemplate: template.DraftSubjectTemplate);
        await _revisionsRepository.InsertAsync(revision, autoSave: true, cancellationToken);
        return revision;
    }

    private async Task<int> GetRevisionCountAsync(Guid templateId, CancellationToken cancellationToken)
    {
        return await (await _revisionsRepository.GetQueryableAsync())
            .CountAsync(x => x.TemplateId == templateId, cancellationToken);
    }

}
