using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.Templates;

public class NotificationTemplateRevision : FullAuditedAggregateRoot<Guid>
{
    public Guid TemplateId { get; private set; }
    public int RevisionNumber { get; private set; }
    public string DisplayName { get; private set; } = default!;
    public string Category { get; private set; } = default!;
    public string? SubjectTemplate { get; private set; }
    public string BodyTemplate { get; private set; } = default!;

    protected NotificationTemplateRevision()
    {
    }

    public NotificationTemplateRevision(
        Guid id,
        Guid templateId,
        int revisionNumber,
        string displayName,
        string category,
        string bodyTemplate,
        string? subjectTemplate = null)
        : base(id)
    {
        TemplateId = templateId;
        RevisionNumber = revisionNumber <= 0 ? throw new ArgumentOutOfRangeException(nameof(revisionNumber)) : revisionNumber;
        DisplayName = global::Shared.Check.NotNullOrWhiteSpace(displayName, nameof(displayName), maxLength: 256);
        Category = global::Shared.Check.NotNullOrWhiteSpace(category, nameof(category), maxLength: 128);
        BodyTemplate = global::Shared.Check.NotNullOrWhiteSpace(bodyTemplate, nameof(bodyTemplate));

        var normalizedSubject = subjectTemplate?.Trim();
        if (normalizedSubject != null && normalizedSubject.Length > 2048)
        {
            throw new ArgumentException("subjectTemplate length must be equal to or lower than 2048!", nameof(subjectTemplate));
        }

        SubjectTemplate = string.IsNullOrWhiteSpace(normalizedSubject) ? null : normalizedSubject;
    }
}
