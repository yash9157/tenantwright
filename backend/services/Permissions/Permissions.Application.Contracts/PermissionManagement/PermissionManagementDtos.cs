using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.PermissionManagement;

public class GetPermissionListResultDto
{
    public string EntityDisplayName { get; set; } = default!;
    public string ProviderVersion { get; set; } = default!;
    public List<PermissionGroupDto> Groups { get; set; } = new();
}

public class PermissionGroupDto
{
    public string Name { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public List<PermissionGrantInfoDto> Permissions { get; set; } = new();
}

public class PermissionGrantInfoDto
{
    public string Name { get; set; } = default!;
    public string? DisplayName { get; set; }
    public string? ParentName { get; set; }
    public bool IsGranted { get; set; }
    public bool? IsEffectiveGranted { get; set; }
    public PermissionGrantEffect? Effect { get; set; }
    public PermissionDecisionSource? EffectiveSource { get; set; }
    public string? EffectiveSourceLabel { get; set; }
    public List<string> AllowedProviders { get; set; } = new();
    public List<GrantedProviderDto> GrantedProviders { get; set; } = new();
}

public class GrantedProviderDto
{
    public string ProviderName { get; set; } = default!;
    public string ProviderKey { get; set; } = default!;
    public PermissionGrantEffect Effect { get; set; }
}

public class UpdatePermissionsDto
{
    public string ExpectedProviderVersion { get; set; } = default!;
    public List<UpdatePermissionDto> Permissions { get; set; } = new();
}

public class UpdatePermissionDto
{
    public string Name { get; set; } = default!;
    public bool IsGranted { get; set; }
    public PermissionGrantEffect Effect { get; set; } = PermissionGrantEffect.Allow;

    /// <summary>Optional expiry for a time-bound grant. Null means the grant never expires.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Optional free-text reason recorded for this grant change.</summary>
    public string? Reason { get; set; }

    /// <summary>Optional external ticket/change-request identifier associated with this grant change.</summary>
    public string? TicketId { get; set; }
}
