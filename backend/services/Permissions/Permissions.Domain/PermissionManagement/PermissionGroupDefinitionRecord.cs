using Shared.Domain;
using Shared.Domain.Entities;
using Shared.AuditLogs.Auditing;

namespace Permissions.Domain.PermissionManagement;

[Audited]
public class PermissionGroupDefinitionRecord : BasicAggregateRoot<Guid>, IHasExtraProperties
{
    public string Name { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public ExtraPropertyDictionary ExtraProperties { get; private set; } = new();

    protected PermissionGroupDefinitionRecord()
    {
    }

    public PermissionGroupDefinitionRecord(Guid id, string name, string displayName)
        : base(id)
    {
        Name = name.Trim();
        DisplayName = displayName.Trim();
        ExtraProperties = new ExtraPropertyDictionary();
    }

    public void PatchFrom(PermissionGroupDefinitionRecord other)
    {
        Name = other.Name;
        DisplayName = other.DisplayName;
        ExtraProperties = new ExtraPropertyDictionary(other.ExtraProperties);
    }
}
