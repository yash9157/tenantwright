using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.Templates;

public class NotificationTemplateContentBlock : FullAuditedAggregateRoot<Guid>
{
    public string Key { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string Category { get; private set; } = default!;
    public string? Description { get; private set; }
    public string? SubjectSnippet { get; private set; }
    public string? BodySnippet { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTime? ArchivedAtUtc { get; private set; }
    public Guid? ArchivedByUserId { get; private set; }

    protected NotificationTemplateContentBlock()
    {
    }

    public NotificationTemplateContentBlock(
        Guid id,
        string key,
        string displayName,
        string category,
        string? description,
        string? subjectSnippet,
        string? bodySnippet)
        : base(id)
    {
        Update(key, displayName, category, description, subjectSnippet, bodySnippet);
    }

    public void Update(
        string key,
        string displayName,
        string category,
        string? description,
        string? subjectSnippet,
        string? bodySnippet)
    {
        Key = NormalizeRequired(key, nameof(key), 128);
        DisplayName = NormalizeRequired(displayName, nameof(displayName), 256);
        Category = NormalizeRequired(category, nameof(category), 128);
        Description = NormalizeOptional(description, nameof(description), 512);
        SubjectSnippet = NormalizeOptional(subjectSnippet, nameof(subjectSnippet), 2048);
        BodySnippet = NormalizeOptional(bodySnippet, nameof(bodySnippet), 4096);

        if (string.IsNullOrWhiteSpace(SubjectSnippet) && string.IsNullOrWhiteSpace(BodySnippet))
        {
            throw new ArgumentException("At least one subject or body snippet is required.");
        }
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
}
