using Permissions.Application.Contracts.Definitions;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.PermissionCatalog;

public enum PermissionCatalogSource
{
    StaticDefinition = 1,
    Dynamic = 2
}

public sealed class PermissionCatalogEntryDto
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = default!;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string GroupName { get; set; } = default!;
    public string? ParentName { get; set; }
    public PermissionElementType? ElementType { get; set; }
    public string? ElementKey { get; set; }
    public bool IsEnabled { get; set; }
    public bool AllowsWildcard { get; set; }
    public bool RequiresApproval { get; set; }
    public List<string> RequiredFeatures { get; set; } = [];
    public List<string> AllowedProviders { get; set; } = [];
    public List<string> AllowedActions { get; set; } = [];
    public int SortOrder { get; set; }
    public PermissionCatalogSource Source { get; set; }
    public bool IsMutable { get; set; }
    public long? Version { get; set; }

    /// <summary>How many active grants reference this capability across all tenants.</summary>
    public long ActiveGrantCount { get; set; }

    /// <summary>How many distinct grant targets (tenant + provider + provider key) hold this capability.</summary>
    public long AffectedProviderCount { get; set; }

    /// <summary>How many pending grant change requests reference this capability.</summary>
    public long PendingChangeCount { get; set; }

    /// <summary>Child capabilities nested by <see cref="ParentName"/>, so the catalog can be returned
    /// pre-grouped and pre-nested and a client renders the tree directly.</summary>
    public List<PermissionCatalogEntryDto> Children { get; set; } = [];
}

/// <summary>A capability group (service) with its capabilities pre-nested into a tree.</summary>
public sealed class PermissionCatalogGroupDto
{
    public string Name { get; set; } = default!;
    public string DisplayName { get; set; } = default!;

    /// <summary>Total capabilities in the group (all nesting levels), and how many are dynamic.</summary>
    public int Total { get; set; }
    public int DynamicCount { get; set; }

    /// <summary>Root capabilities of the group; descendants hang off each entry's <c>Children</c>.</summary>
    public List<PermissionCatalogEntryDto> Capabilities { get; set; } = [];
}

/// <summary>The permission catalog grouped by service and nested by parent capability.</summary>
public sealed class GetPermissionCatalogResultDto
{
    public List<PermissionCatalogGroupDto> Groups { get; set; } = [];
}

public sealed class CreatePermissionCatalogEntryDto
{
    public string Name { get; set; } = default!;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string GroupName { get; set; } = default!;
    public PermissionElementType ElementType { get; set; }
    public string ElementKey { get; set; } = default!;
    public string? ParentName { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool AllowsWildcard { get; set; } = true;
    public bool RequiresApproval { get; set; }
    public List<string> RequiredFeatures { get; set; } = [];
    public List<string> AllowedProviders { get; set; } = [];
    public List<string> AllowedActions { get; set; } = [];
    public int SortOrder { get; set; }
}

public sealed class UpdatePermissionCatalogEntryDto
{
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string GroupName { get; set; } = default!;
    public string? ParentName { get; set; }
    public bool IsEnabled { get; set; }
    public bool AllowsWildcard { get; set; }
    public bool RequiresApproval { get; set; }
    public List<string> RequiredFeatures { get; set; } = [];
    public List<string> AllowedProviders { get; set; } = [];
    public List<string> AllowedActions { get; set; } = [];
    public int SortOrder { get; set; }
    public long ExpectedVersion { get; set; }
}
