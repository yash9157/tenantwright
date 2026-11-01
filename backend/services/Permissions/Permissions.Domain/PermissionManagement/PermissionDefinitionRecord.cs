using Shared.Domain;
using Shared.Domain.Entities;
using Shared.AuditLogs.Auditing;

namespace Permissions.Domain.PermissionManagement;

[Audited]
public class PermissionDefinitionRecord : BasicAggregateRoot<Guid>, IHasExtraProperties
{
    public string? GroupName { get; private set; }
    public string Name { get; private set; } = default!;
    public string? ResourceName { get; private set; }
    public string? ManagementPermissionName { get; private set; }
    public string? ParentName { get; private set; }
    public string DisplayName { get; private set; } = default!;
    public bool IsEnabled { get; private set; }
    public byte MultiTenancySide { get; private set; }
    public string? Providers { get; private set; }
    public string? StateCheckers { get; private set; }
    public ExtraPropertyDictionary ExtraProperties { get; private set; } = new();

    protected PermissionDefinitionRecord()
    {
    }

    public PermissionDefinitionRecord(
        Guid id,
        string? groupName,
        string name,
        string? resourceName,
        string? managementPermissionName,
        string? parentName,
        string displayName,
        bool isEnabled,
        byte multiTenancySide,
        string? providers,
        string? stateCheckers)
        : base(id)
    {
        GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
        Name = name.Trim();
        ResourceName = string.IsNullOrWhiteSpace(resourceName) ? null : resourceName.Trim();
        ManagementPermissionName = string.IsNullOrWhiteSpace(managementPermissionName) ? null : managementPermissionName.Trim();
        ParentName = string.IsNullOrWhiteSpace(parentName) ? null : parentName.Trim();
        DisplayName = displayName.Trim();
        IsEnabled = isEnabled;
        MultiTenancySide = multiTenancySide;
        Providers = string.IsNullOrWhiteSpace(providers) ? null : providers.Trim();
        StateCheckers = string.IsNullOrWhiteSpace(stateCheckers) ? null : stateCheckers.Trim();
        ExtraProperties = new ExtraPropertyDictionary();
    }

    public void PatchFrom(PermissionDefinitionRecord other)
    {
        GroupName = other.GroupName;
        Name = other.Name;
        ResourceName = other.ResourceName;
        ManagementPermissionName = other.ManagementPermissionName;
        ParentName = other.ParentName;
        DisplayName = other.DisplayName;
        IsEnabled = other.IsEnabled;
        MultiTenancySide = other.MultiTenancySide;
        Providers = other.Providers;
        StateCheckers = other.StateCheckers;
        ExtraProperties = new ExtraPropertyDictionary(other.ExtraProperties);
    }
}
