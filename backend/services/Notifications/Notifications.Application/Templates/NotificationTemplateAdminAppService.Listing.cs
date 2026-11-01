using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Templates;
using Notifications.Domain.Templates;
using Scriban;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notifications.Application.Templates;

internal sealed partial class NotificationTemplateAdminAppService
{
    public async Task<PagedResultDto<NotificationTemplateListItemDto>> GetListAsync(
        GetNotificationTemplatesInput input,
        CancellationToken cancellationToken = default)
    {
        await _importService.EnsureImportedAsync(cancellationToken);
        var templates = await (await _definitionsRepository.GetQueryableAsync())
            .OrderBy(x => x.Category)
            .ThenBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        templates = ApplyArchiveFilter(templates, input.ArchiveState);

        if (!string.IsNullOrWhiteSpace(input.Filter))
        {
            var filter = input.Filter.Trim();
            templates = templates
                .Where(template =>
                    template.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    template.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    template.Category.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var templateIds = templates.Select(template => template.Id).ToList();
        var revisionCounts = templateIds.Count == 0
            ? new Dictionary<Guid, int>()
            : (await (await _revisionsRepository.GetQueryableAsync())
                    .Where(revision => templateIds.Contains(revision.TemplateId))
                    .Select(revision => revision.TemplateId)
                    .ToListAsync(cancellationToken))
                .GroupBy(templateId => templateId)
                .ToDictionary(group => group.Key, group => group.Count());

        var skip = Math.Max(input.SkipCount, 0);
        var take = input.MaxResultCount <= 0 ? 50 : input.MaxResultCount;

        return new PagedResultDto<NotificationTemplateListItemDto>
        {
            TotalCount = templates.Count,
            Items = templates
                .Skip(skip)
                .Take(take)
                .Select(template => new NotificationTemplateListItemDto
                {
                    Name = template.Name,
                    DisplayName = template.DisplayName,
                    Category = template.Category,
                    RelativePath = template.RelativePath,
                    SourceType = GetSourceType(template),
                    SupportsPreview = true,
                    RevisionCount = revisionCounts.GetValueOrDefault(template.Id),
                    PublishedRevisionNumber = template.PublishedRevisionNumber,
                    PublishedAtUtc = template.PublishedAtUtc,
                    IsArchived = template.IsArchived,
                    ArchivedAtUtc = template.ArchivedAtUtc,
                    ArchivedByUserId = template.ArchivedByUserId
                })
                .ToList()
        };
    }

    public async Task<NotificationTemplateDetailDto> GetAsync(string templateName, CancellationToken cancellationToken = default)
    {
        var template = await GetDefinitionAsync(templateName, cancellationToken);
        return new NotificationTemplateDetailDto
        {
            Name = template.Name,
            DisplayName = template.DisplayName,
            Category = template.Category,
            RelativePath = template.RelativePath,
            SourceType = GetSourceType(template),
            SupportsPreview = true,
            IsArchived = template.IsArchived,
            ArchivedAtUtc = template.ArchivedAtUtc,
            ArchivedByUserId = template.ArchivedByUserId
        };
    }

    public Task<RenderTemplateResultDto> PreviewAsync(RenderTemplateRequestDto input, CancellationToken cancellationToken = default)
    {
        return _templateRendererAppService.RenderAsync(input, cancellationToken);
    }

}
