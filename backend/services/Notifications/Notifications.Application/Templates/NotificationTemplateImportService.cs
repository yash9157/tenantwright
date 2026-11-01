using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Templates;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Templates;

[DisableConventionalRegistration]
internal sealed class NotificationTemplateImportService : ITransientDependency
{
    private static readonly SemaphoreSlim ImportLock = new(1, 1);

    private readonly IRepository<NotificationTemplateDefinition, Guid> _definitionsRepository;
    private readonly IRepository<NotificationTemplateRevision, Guid> _revisionsRepository;
    private readonly IFileSystemTemplateSource _fileSystemTemplateSource;

    public NotificationTemplateImportService(
        IRepository<NotificationTemplateDefinition, Guid> definitionsRepository,
        IRepository<NotificationTemplateRevision, Guid> revisionsRepository,
        IFileSystemTemplateSource fileSystemTemplateSource)
    {
        _definitionsRepository = definitionsRepository;
        _revisionsRepository = revisionsRepository;
        _fileSystemTemplateSource = fileSystemTemplateSource;
    }

    public async Task EnsureImportedAsync(CancellationToken cancellationToken = default)
    {
        await ImportLock.WaitAsync(cancellationToken);
        try
        {
            var existingNames = (await (await _definitionsRepository.GetQueryableAsync())
                    .Select(x => x.Name)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var descriptor in _fileSystemTemplateSource.DiscoverTemplates())
            {
                if (existingNames.Contains(descriptor.Name))
                {
                    continue;
                }

                var content = await _fileSystemTemplateSource.GetAsync(descriptor.Name, cancellationToken);
                if (content == null)
                {
                    continue;
                }

                var definition = new NotificationTemplateDefinition(
                    id: Guid.CreateVersion7(),
                    name: descriptor.Name,
                    displayName: descriptor.DisplayName,
                    category: descriptor.Category,
                    relativePath: descriptor.RelativePath,
                    draftBodyTemplate: content.BodyTemplate,
                    draftSubjectTemplate: content.SubjectTemplate);
                var revision = new NotificationTemplateRevision(
                    id: Guid.CreateVersion7(),
                    templateId: definition.Id,
                    revisionNumber: 1,
                    displayName: definition.DisplayName,
                    category: definition.Category,
                    bodyTemplate: definition.DraftBodyTemplate,
                    subjectTemplate: definition.DraftSubjectTemplate);
                definition.MarkPublished(revision, DateTime.UtcNow);

                await _definitionsRepository.InsertAsync(definition, autoSave: true, cancellationToken);
                await _revisionsRepository.InsertAsync(revision, autoSave: true, cancellationToken);
                existingNames.Add(descriptor.Name);
            }
        }
        finally
        {
            ImportLock.Release();
        }
    }
}
