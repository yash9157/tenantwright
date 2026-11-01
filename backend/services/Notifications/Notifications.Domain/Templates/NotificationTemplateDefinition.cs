using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.Templates;

public class NotificationTemplateDefinition : FullAuditedAggregateRoot<Guid>
{
    public string Name { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string Category { get; private set; } = default!;
    public string RelativePath { get; private set; } = default!;
    public string? DraftSubjectTemplate { get; private set; }
    public string DraftBodyTemplate { get; private set; } = default!;
    public Guid? PublishedRevisionId { get; private set; }
    public int? PublishedRevisionNumber { get; private set; }
    public DateTime? PublishedAtUtc { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTime? ArchivedAtUtc { get; private set; }
    public Guid? ArchivedByUserId { get; private set; }

    protected NotificationTemplateDefinition()
    {
    }

    public NotificationTemplateDefinition(
        Guid id,
        string name,
        string displayName,
        string category,
        string relativePath,
        string draftBodyTemplate,
        string? draftSubjectTemplate = null)
        : base(id)
    {
        Name = NormalizeRequired(name, nameof(name), 256);
        DisplayName = NormalizeRequired(displayName, nameof(displayName), 256);
        Category = NormalizeRequired(category, nameof(category), 128);
        RelativePath = NormalizeRequired(relativePath, nameof(relativePath), 512);
        DraftBodyTemplate = global::Shared.Check.NotNullOrWhiteSpace(draftBodyTemplate, nameof(draftBodyTemplate));
        DraftSubjectTemplate = NormalizeOptional(draftSubjectTemplate, nameof(draftSubjectTemplate), 2048);
    }

    public void UpdateDraft(
        string displayName,
        string category,
        string bodyTemplate,
        string? subjectTemplate)
    {
        DisplayName = NormalizeRequired(displayName, nameof(displayName), 256);
        Category = NormalizeRequired(category, nameof(category), 128);
        DraftBodyTemplate = global::Shared.Check.NotNullOrWhiteSpace(bodyTemplate, nameof(bodyTemplate));
        DraftSubjectTemplate = NormalizeOptional(subjectTemplate, nameof(subjectTemplate), 2048);
    }

    public void MarkPublished(NotificationTemplateRevision revision, DateTime publishedAtUtc)
    {
        if (revision.TemplateId != Id)
        {
            throw new InvalidOperationException("Revision does not belong to the selected template.");
        }

        PublishedRevisionId = revision.Id;
        PublishedRevisionNumber = revision.RevisionNumber;
        PublishedAtUtc = publishedAtUtc;
    }

    public void RenameDraftOnly(string name, string relativePath)
    {
        Name = NormalizeRequired(name, nameof(name), 256);
        RelativePath = NormalizeRequired(relativePath, nameof(relativePath), 512);
    }

    public void Archive(DateTime archivedAtUtc, Guid? archivedByUserId = null)
    {
        if (IsArchived)
        {
            return;
        }

        IsArchived = true;
        ArchivedAtUtc = archivedAtUtc;
        ArchivedByUserId = archivedByUserId;
    }

    public void Restore()
    {
        if (!IsArchived)
        {
            return;
        }

        IsArchived = false;
        ArchivedAtUtc = null;
        ArchivedByUserId = null;
    }

    private static string NormalizeRequired(string value, string paramName, int maxLength)
    {
        return global::Shared.Check.NotNullOrWhiteSpace(value, paramName, maxLength: maxLength);
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
}
