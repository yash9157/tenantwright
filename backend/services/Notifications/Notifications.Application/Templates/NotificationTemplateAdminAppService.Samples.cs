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
    public async Task<List<NotificationTemplateSampleDto>> GetSamplesAsync(
        string templateName,
        CancellationToken cancellationToken = default)
    {
        var template = await GetDefinitionAsync(templateName, cancellationToken);
        var samples = await (await _samplesRepository.GetQueryableAsync())
            .Where(x => x.TemplateId == template.Id)
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return samples.Select(sample => MapSample(template, sample)).ToList();
    }

    public async Task<NotificationTemplateSampleDto> CreateSampleAsync(
        CreateNotificationTemplateSampleInput input,
        CancellationToken cancellationToken = default)
    {
        var template = await GetDefinitionAsync(input.TemplateName, cancellationToken);
        var name = NormalizeRequired(input.Name, nameof(input.Name), 128);
        await EnsureUniqueSampleNameAsync(template.Id, name, excludedSampleId: null, cancellationToken);

        if (input.IsDefault)
        {
            await ClearDefaultSamplesAsync(template.Id, excludedSampleId: null, cancellationToken);
        }

        var sample = new NotificationTemplateSample(
            id: Guid.CreateVersion7(),
            templateId: template.Id,
            name: name,
            modelJson: SerializeSampleModel(input.Model),
            description: NormalizeOptional(input.Description, nameof(input.Description), 512),
            isDefault: input.IsDefault);

        await _samplesRepository.InsertAsync(sample, autoSave: true, cancellationToken);
        return MapSample(template, sample);
    }

    public async Task<NotificationTemplateSampleDto> UpdateSampleAsync(
        UpdateNotificationTemplateSampleInput input,
        CancellationToken cancellationToken = default)
    {
        var sample = await GetSampleAsync(input.Id, cancellationToken);
        var template = await GetDefinitionByIdAsync(sample.TemplateId, cancellationToken);
        var name = NormalizeRequired(input.Name, nameof(input.Name), 128);
        await EnsureUniqueSampleNameAsync(template.Id, name, sample.Id, cancellationToken);

        if (input.IsDefault)
        {
            await ClearDefaultSamplesAsync(template.Id, sample.Id, cancellationToken);
        }

        sample.Update(
            name,
            SerializeSampleModel(input.Model),
            NormalizeOptional(input.Description, nameof(input.Description), 512),
            input.IsDefault);

        await _samplesRepository.UpdateAsync(sample, autoSave: true, cancellationToken);
        return MapSample(template, sample);
    }

    public async Task<NotificationTemplateSampleDto> SetDefaultSampleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var sample = await GetSampleAsync(id, cancellationToken);
        var template = await GetDefinitionByIdAsync(sample.TemplateId, cancellationToken);

        await ClearDefaultSamplesAsync(template.Id, sample.Id, cancellationToken);
        sample.MarkDefault();
        await _samplesRepository.UpdateAsync(sample, autoSave: true, cancellationToken);

        return MapSample(template, sample);
    }

    public async Task DeleteSampleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var sample = await GetSampleAsync(id, cancellationToken);
        await _samplesRepository.DeleteAsync(sample, autoSave: true, cancellationToken);
    }

}
