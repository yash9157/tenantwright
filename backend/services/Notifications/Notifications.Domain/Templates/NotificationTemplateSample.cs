using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.Templates;

public class NotificationTemplateSample : FullAuditedAggregateRoot<Guid>
{
    public Guid TemplateId { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public string ModelJson { get; private set; } = "{}";
    public bool IsDefault { get; private set; }

    protected NotificationTemplateSample()
    {
    }

    public NotificationTemplateSample(
        Guid id,
        Guid templateId,
        string name,
        string modelJson,
        string? description = null,
        bool isDefault = false)
        : base(id)
    {
        TemplateId = templateId;
        Rename(name);
        UpdateModel(modelJson, description);
        IsDefault = isDefault;
    }

    public void Update(string name, string modelJson, string? description, bool isDefault)
    {
        Rename(name);
        UpdateModel(modelJson, description);
        IsDefault = isDefault;
    }

    public void MarkDefault()
    {
        IsDefault = true;
    }

    public void ClearDefault()
    {
        IsDefault = false;
    }

    private void Rename(string name)
    {
        Name = global::Shared.Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: 128).Trim();
    }

    private void UpdateModel(string modelJson, string? description)
    {
        ModelJson = global::Shared.Check.NotNullOrWhiteSpace(modelJson, nameof(modelJson));

        var normalizedDescription = description?.Trim();
        if (normalizedDescription != null && normalizedDescription.Length > 512)
        {
            throw new ArgumentException("description length must be equal to or lower than 512!", nameof(description));
        }

        Description = string.IsNullOrWhiteSpace(normalizedDescription) ? null : normalizedDescription;
    }
}
