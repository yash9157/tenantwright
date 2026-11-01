using Identity.Domain.Shared.ApplicationUsers;

namespace Identity.Application.Contracts.Platform;

public sealed class PermissionAssignmentTargetSearchInput : Users.PagedRequestDto
{
    public string? Filter { get; set; }
    /// <summary>
    /// Optional tenant scope for a host administrator managing an explicitly selected tenant.
    /// Omit for host staff and platform-role targets.
    /// </summary>
    public Guid? TenantId { get; set; }
}

public sealed class PermissionAssignmentUserTargetDto
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? Name { get; set; }
    public string? Surname { get; set; }
    public bool IsActive { get; set; }
    public List<string> RoleNames { get; set; } = [];
}

public sealed class PermissionAssignmentRoleTargetDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public bool IsSensitive { get; set; }
    public ApplicationRoleLifecycleState LifecycleState { get; set; }
    public long LifecycleVersion { get; set; }
}

public interface IPermissionAssignmentTargetSearchAppService
{
    Task<Users.PagedResultDto<PermissionAssignmentUserTargetDto>> GetUsersAsync(PermissionAssignmentTargetSearchInput input, CancellationToken cancellationToken = default);
    Task<Users.PagedResultDto<PermissionAssignmentRoleTargetDto>> GetRolesAsync(PermissionAssignmentTargetSearchInput input, CancellationToken cancellationToken = default);
}
