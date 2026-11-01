using Permissions.Application.Contracts.Definitions;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.TenantPermissionAccess;

public enum TenantPermissionGrantState
{
    All = 0,
    Granted = 1,
    Denied = 2
}

public class GetCurrentTenantPermissionAccessInput
{
    public string? Filter { get; set; }
    public string? GroupName { get; set; }
    public TenantPermissionGrantState GrantState { get; set; } = TenantPermissionGrantState.All;
}

public class TenantPermissionAccessDto
{
    /// <summary>The tenant scope of this projection, or null for the host/current-principal scope.</summary>
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public string? UserName { get; set; }
    public List<string> RoleNames { get; set; } = new();
    public string SnapshotHash { get; set; } = default!;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public TenantPermissionAccessSummaryDto Summary { get; set; } = new();
    public List<TenantPermissionAccessGroupDto> Groups { get; set; } = new();
}

public class TenantPermissionAccessSummaryDto
{
    public int TotalCount { get; set; }
    public int GrantedCount { get; set; }
    public int DeniedCount { get; set; }
    public int FeatureDeniedCount { get; set; }
    public int GroupCount { get; set; }
}

public class TenantPermissionAccessGroupDto
{
    public string Name { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public int TotalCount { get; set; }
    public int GrantedCount { get; set; }
    public int DeniedCount { get; set; }
    public List<TenantPermissionAccessItemDto> Permissions { get; set; } = new();
}

public class TenantPermissionAccessItemDto
{
    public string Name { get; set; } = default!;
    public string? DisplayName { get; set; }
    public string? ParentName { get; set; }
    public bool IsGranted { get; set; }
    public bool IsEnabled { get; set; }
    public PermissionDecisionSource Source { get; set; }
    public string SourceLabel { get; set; } = default!;
    public PermissionElementType? ElementType { get; set; }
    public string? ElementKey { get; set; }
    public List<string> RequiredFeatures { get; set; } = new();
}
