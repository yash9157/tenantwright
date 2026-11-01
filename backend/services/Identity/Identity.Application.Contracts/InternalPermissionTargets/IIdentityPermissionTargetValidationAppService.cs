using Identity.Domain.Shared.ApplicationUsers;

namespace Identity.Application.Contracts.InternalPermissionTargets;

public sealed class RolePermissionTargetDto
{
    public string Name { get; set; } = default!;
    public bool IsStatic { get; set; }
    public Guid Id { get; set; }
    public ApplicationRoleLifecycleState LifecycleState { get; set; }
    public long LifecycleVersion { get; set; }
}

public sealed class UserPermissionTargetDto
{
    public string Id { get; set; } = default!;
    public string UserName { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public bool IsActive { get; set; }
    public bool IsLockedOut { get; set; }
    public List<Guid> RoleIds { get; set; } = [];
}

public enum PermissionTargetKind
{
    User = 1,
    Role = 2
}

public sealed class ResolvePermissionTargetsInput
{
    public List<ResolvePermissionTargetInput> Targets { get; set; } = [];
}

public sealed class ResolvePermissionTargetInput
{
    public PermissionTargetKind Kind { get; set; }
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
}

public sealed class ResolvedPermissionTargetDto
{
    public PermissionTargetKind Kind { get; set; }
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string DisplayName { get; set; } = default!;
}

public interface IIdentityPermissionTargetValidationAppService
{
    Task<RolePermissionTargetDto?> GetRoleAsync(Guid roleId, Guid? tenantId, CancellationToken cancellationToken = default);
    Task<UserPermissionTargetDto?> GetUserAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken = default);
    Task<List<ResolvedPermissionTargetDto>> ResolveAsync(ResolvePermissionTargetsInput input, CancellationToken cancellationToken = default);
}
