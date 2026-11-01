using Shared.Domain;
using Shared.Domain.Entities;
using Shared.AuditLogs.Auditing;

namespace Permissions.Domain.FeatureManagement;

[Audited]
public class FeatureGroupDefinitionRecord : BasicAggregateRoot<Guid>, IHasExtraProperties
{
    public string Name { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public ExtraPropertyDictionary ExtraProperties { get; private set; } = new();

    protected FeatureGroupDefinitionRecord()
    {
    }

    public FeatureGroupDefinitionRecord(Guid id, string name, string displayName)
        : base(id)
    {
        Name = name.Trim();
        DisplayName = displayName.Trim();
        ExtraProperties = new ExtraPropertyDictionary();
    }

    public void PatchFrom(FeatureGroupDefinitionRecord other)
    {
        Name = other.Name;
        DisplayName = other.DisplayName;
        ExtraProperties = new ExtraPropertyDictionary(other.ExtraProperties);
    }
}
