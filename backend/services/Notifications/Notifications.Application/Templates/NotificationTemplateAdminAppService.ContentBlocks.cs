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
    public async Task<PagedResultDto<NotificationTemplateContentBlockDto>> GetContentBlocksAsync(
        GetNotificationTemplateContentBlocksInput input,
        CancellationToken cancellationToken = default)
    {
        var blocks = await (await _contentBlocksRepository.GetQueryableAsync())
            .OrderBy(x => x.Category)
            .ThenBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        blocks = ApplyContentBlockArchiveFilter(blocks, input.ArchiveState);

        if (!string.IsNullOrWhiteSpace(input.Category))
        {
            var category = input.Category.Trim();
            blocks = blocks
                .Where(block => string.Equals(block.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(input.Filter))
        {
            var filter = input.Filter.Trim();
            blocks = blocks
                .Where(block =>
                    block.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    block.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    block.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (block.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        var skip = Math.Max(input.SkipCount, 0);
        var take = input.MaxResultCount <= 0 ? 50 : input.MaxResultCount;

        return new PagedResultDto<NotificationTemplateContentBlockDto>
        {
            TotalCount = blocks.Count,
            Items = blocks
                .Skip(skip)
                .Take(take)
                .Select(MapContentBlock)
                .ToList()
        };
    }

    public async Task<NotificationTemplateContentBlockDto> CreateContentBlockAsync(
        CreateNotificationTemplateContentBlockInput input,
        CancellationToken cancellationToken = default)
    {
        var key = NormalizeRequired(input.Key, nameof(input.Key), 128);
        await EnsureUniqueContentBlockKeyAsync(key, excludedBlockId: null, cancellationToken);

        var block = new NotificationTemplateContentBlock(
            id: Guid.CreateVersion7(),
            key: key,
            displayName: NormalizeRequired(input.DisplayName, nameof(input.DisplayName), 256),
            category: NormalizeRequired(input.Category, nameof(input.Category), 128),
            description: NormalizeOptional(input.Description, nameof(input.Description), 512),
            subjectSnippet: NormalizeOptional(input.SubjectSnippet, nameof(input.SubjectSnippet), 2048),
            bodySnippet: NormalizeOptional(input.BodySnippet, nameof(input.BodySnippet), 4096));

        await _contentBlocksRepository.InsertAsync(block, autoSave: true, cancellationToken);
        return MapContentBlock(block);
    }

    public async Task<NotificationTemplateContentBlockDto> UpdateContentBlockAsync(
        UpdateNotificationTemplateContentBlockInput input,
        CancellationToken cancellationToken = default)
    {
        var block = await GetContentBlockAsync(input.Id, cancellationToken);
        var key = NormalizeRequired(input.Key, nameof(input.Key), 128);
        await EnsureUniqueContentBlockKeyAsync(key, block.Id, cancellationToken);

        block.Update(
            key,
            NormalizeRequired(input.DisplayName, nameof(input.DisplayName), 256),
            NormalizeRequired(input.Category, nameof(input.Category), 128),
            NormalizeOptional(input.Description, nameof(input.Description), 512),
            NormalizeOptional(input.SubjectSnippet, nameof(input.SubjectSnippet), 2048),
            NormalizeOptional(input.BodySnippet, nameof(input.BodySnippet), 4096));

        await _contentBlocksRepository.UpdateAsync(block, autoSave: true, cancellationToken);
        return MapContentBlock(block);
    }

    public async Task<NotificationTemplateContentBlockDto> ArchiveContentBlockAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var block = await GetContentBlockAsync(id, cancellationToken);
        block.Archive(DateTime.UtcNow);

        await _contentBlocksRepository.UpdateAsync(block, autoSave: true, cancellationToken);
        return MapContentBlock(block);
    }

    public async Task<NotificationTemplateContentBlockDto> RestoreContentBlockAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var block = await GetContentBlockAsync(id, cancellationToken);
        block.Restore();

        await _contentBlocksRepository.UpdateAsync(block, autoSave: true, cancellationToken);
        return MapContentBlock(block);
    }

}
