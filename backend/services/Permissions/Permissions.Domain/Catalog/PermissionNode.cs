using Permissions.Application.Contracts.Definitions;
using Shared;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.AuditLogs.Auditing;

namespace Permissions.Domain.Catalog;

[Audited]
public class PermissionNode : AuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public string Name { get; private set; } = default!;

    public string? DisplayName { get; private set; }

    public string? Description { get; private set; }

    public string GroupName { get; private set; } = default!;

    public PermissionElementType ElementType { get; private set; }

    public string ElementKey { get; private set; } = default!;

    public string? ParentName { get; private set; }

    public bool IsEnabled { get; private set; }

    public bool AllowsWildcard { get; private set; }

    public bool RequiresApproval { get; private set; }

    public string[] RequiredFeatures { get; private set; } = [];

    public string[] AllowedProviders { get; private set; } = [];

    public string[] AllowedActions { get; private set; } = [];

    public int SortOrder { get; private set; }

    public long Version { get; private set; }

    protected PermissionNode()
    {
    }

    public PermissionNode(
        Guid id,
        Guid? tenantId,
        string name,
        PermissionElementType elementType,
        string elementKey,
        string groupName,
        string? displayName = null,
        string? description = null,
        string? parentName = null,
        bool isEnabled = true,
        bool allowsWildcard = true,
        bool requiresApproval = false,
        IEnumerable<string>? requiredFeatures = null,
        IEnumerable<string>? allowedProviders = null,
        IEnumerable<string>? allowedActions = null,
        int sortOrder = 0)
        : base(id)
    {
        TenantId = tenantId;
        Name = Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: PermissionsConsts.MaxPermissionNameLength);
        ElementType = elementType;
        ElementKey = Check.NotNullOrWhiteSpace(elementKey, nameof(elementKey), maxLength: PermissionsConsts.MaxElementKeyLength);
        GroupName = Check.NotNullOrWhiteSpace(groupName, nameof(groupName), maxLength: PermissionsConsts.MaxGroupNameLength);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : Check.NotNull(displayName, nameof(displayName), maxLength: PermissionsConsts.MaxDisplayNameLength);
        Description = NormalizeOptional(description, nameof(description), PermissionsConsts.MaxDescriptionLength);
        ParentName = string.IsNullOrWhiteSpace(parentName) ? null : Check.NotNull(parentName, nameof(parentName), maxLength: PermissionsConsts.MaxPermissionNameLength);
        IsEnabled = isEnabled;
        AllowsWildcard = allowsWildcard;
        RequiresApproval = requiresApproval;
        RequiredFeatures = NormalizeValues(requiredFeatures);
        AllowedProviders = NormalizeValues(allowedProviders);
        AllowedActions = NormalizeValues(allowedActions);
        SortOrder = sortOrder;
        Version = 1;
    }

    public void SetDisplayName(string? displayName)
    {
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? null
            : Check.NotNull(displayName, nameof(displayName), maxLength: PermissionsConsts.MaxDisplayNameLength);
    }

    public void UpdateMetadata(
        string? displayName,
        string? description,
        string groupName,
        string? parentName,
        bool isEnabled,
        bool allowsWildcard,
        bool requiresApproval,
        IEnumerable<string>? requiredFeatures,
        IEnumerable<string>? allowedProviders,
        IEnumerable<string>? allowedActions,
        int sortOrder)
    {
        SetDisplayName(displayName);
        Description = NormalizeOptional(description, nameof(description), PermissionsConsts.MaxDescriptionLength);
        GroupName = Check.NotNullOrWhiteSpace(groupName, nameof(groupName), PermissionsConsts.MaxGroupNameLength);
        SetParentName(parentName);
        IsEnabled = isEnabled;
        AllowsWildcard = allowsWildcard;
        RequiresApproval = requiresApproval;
        RequiredFeatures = NormalizeValues(requiredFeatures);
        AllowedProviders = NormalizeValues(allowedProviders);
        AllowedActions = NormalizeValues(allowedActions);
        SortOrder = sortOrder;
        Version++;
    }

    public void SetParentName(string? parentName)
    {
        ParentName = string.IsNullOrWhiteSpace(parentName)
            ? null
            : Check.NotNullOrWhiteSpace(parentName, nameof(parentName), maxLength: PermissionsConsts.MaxPermissionNameLength);
    }

    public void Enable()
    {
        IsEnabled = true;
    }

    public void Disable()
    {
        IsEnabled = false;
    }

    public void SetSortOrder(int sortOrder)
    {
        SortOrder = sortOrder;
    }

    public void SetWildcardAllowed(bool allowsWildcard)
    {
        AllowsWildcard = allowsWildcard;
    }

    private static string? NormalizeOptional(string? value, string parameterName, int maxLength)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Check.NotNullOrWhiteSpace(value, parameterName, maxLength).Trim();
    }

    private static string[] NormalizeValues(IEnumerable<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
    }
}
