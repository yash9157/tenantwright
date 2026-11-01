using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Templates;
using Notifications.Domain.Templates;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Templates;

[DisableConventionalRegistration]
internal sealed class DatabaseBackedTemplateStore : ITemplateStore, ITransientDependency
{
    private readonly IRepository<NotificationTemplateDefinition, Guid> _definitionsRepository;
    private readonly IRepository<NotificationTemplateRevision, Guid> _revisionsRepository;
    private readonly NotificationTemplateImportService _importService;
    private readonly IFileSystemTemplateSource _fileSystemTemplateSource;
    private readonly NotificationsTemplateOptions _options;

    public DatabaseBackedTemplateStore(
        IRepository<NotificationTemplateDefinition, Guid> definitionsRepository,
        IRepository<NotificationTemplateRevision, Guid> revisionsRepository,
        NotificationTemplateImportService importService,
        IFileSystemTemplateSource fileSystemTemplateSource,
        Microsoft.Extensions.Options.IOptions<NotificationsTemplateOptions> options)
    {
        _definitionsRepository = definitionsRepository;
        _revisionsRepository = revisionsRepository;
        _importService = importService;
        _fileSystemTemplateSource = fileSystemTemplateSource;
        _options = options.Value;
    }

    public async Task<StoredNotificationTemplateContent?> GetAsync(string templateName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(templateName))
        {
            return null;
        }

        var normalizedName = templateName.Trim();
        if (!string.Equals(normalizedName, _options.LayoutTemplateName, StringComparison.OrdinalIgnoreCase))
        {
            await _importService.EnsureImportedAsync(cancellationToken);

            var definition = await (await _definitionsRepository.GetQueryableAsync())
                .FirstOrDefaultAsync(x => x.Name == normalizedName, cancellationToken);
            if (definition?.PublishedRevisionId is Guid revisionId)
            {
                var revision = await (await _revisionsRepository.GetQueryableAsync())
                    .FirstOrDefaultAsync(x => x.Id == revisionId, cancellationToken);
                if (revision != null)
                {
                    return new StoredNotificationTemplateContent
                    {
                        TemplateName = normalizedName,
                        BodyTemplate = revision.BodyTemplate,
                        SubjectTemplate = revision.SubjectTemplate,
                        RelativePath = definition.RelativePath,
                        SourceType = "Database"
                    };
                }
            }
        }

        return await _fileSystemTemplateSource.GetAsync(normalizedName, cancellationToken);
    }
}
