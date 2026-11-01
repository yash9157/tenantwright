using Identity.Application.ApplicationUsers;
using Identity.Domain.ApplicationUsers;
using Shared;
using Shared.CurrentUser;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Identity.Application.Users;

public sealed class TenantUserManagementGuard : ITransientDependency
{
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTenant _currentTenant;
    private readonly ApplicationUserManager _userManager;

    public TenantUserManagementGuard(
        ICurrentUser currentUser,
        ICurrentTenant currentTenant,
        ApplicationUserManager userManager)
    {
        _currentUser = currentUser;
        _currentTenant = currentTenant;
        _userManager = userManager;
    }

    public Guid RequireTenantId()
    {
        return _currentTenant.Id ?? throw new SharedValidationException("Tenant context is required.");
    }

    public Guid ResolveTenantId(Guid? requestedTenantId)
    {
        EnsureUserManagementActor();

        if (IsCurrentUserHostAdmin())
        {
            return requestedTenantId ?? _currentTenant.Id ?? throw new SharedValidationException("Tenant context is required.");
        }

        var tenantId = RequireTenantId();
        if (requestedTenantId.HasValue && requestedTenantId.Value != tenantId)
        {
            throw new UnauthorizedAccessException("Cross-tenant user management is not allowed.");
        }

        return tenantId;
    }

    public bool IsCurrentUserHostAdmin()
    {
        return _currentUser.IsInRole(TenantWrightIdentityRoleNames.HostAdmin);
    }

    public bool IsCurrentUserTenantAdmin()
    {
        return _currentUser.Roles.Any(TenantWrightIdentityRoleNames.IsTenantAdminRole);
    }

    public bool IsInternalServiceCaller()
    {
        return _currentUser.IsAuthenticated &&
               !_currentUser.Id.HasValue &&
               (_currentUser.Roles == null || _currentUser.Roles.Length == 0);
    }

    public void EnsureUserManagementActor()
    {
        if (IsInternalServiceCaller())
        {
            return;
        }

        if (IsCurrentUserHostAdmin() || IsCurrentUserTenantAdmin())
        {
            return;
        }

        throw new UnauthorizedAccessException("Tenant-scoped user management requires admin or tenant-admin context.");
    }

    public void EnsureHostManagementActor()
    {
        if (IsInternalServiceCaller())
        {
            return;
        }

        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAccessException("Host management requires an authenticated user.");
        }

        if (_currentTenant.Id.HasValue)
        {
            throw new UnauthorizedAccessException("Host management requires host context.");
        }
    }

    public IReadOnlyList<string> NormalizeRequestedRoleNames(IEnumerable<string>? roleNames)
    {
        return TenantWrightIdentityRoleNames.NormalizeMany(roleNames);
    }

    public void EnsureCanAssignRoles(IReadOnlyCollection<string> normalizedRoleNames)
    {
        EnsureUserManagementActor();

        if (normalizedRoleNames.Any(TenantWrightIdentityRoleNames.IsHostAdminRole))
        {
            throw new UnauthorizedAccessException("Tenant-scoped user management cannot assign host admin.");
        }

        if (!IsCurrentUserHostAdmin() && normalizedRoleNames.Any(TenantWrightIdentityRoleNames.IsTenantAdminRole))
        {
            throw new UnauthorizedAccessException("Tenant-admins cannot assign elevated tenant roles.");
        }
    }

    public async Task EnsureCanManageTargetAsync(ApplicationUser user)
    {
        EnsureUserManagementActor();

        var tenantId = RequireTenantId();
        if (user.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("Cross-tenant user management is not allowed.");
        }

        if (IsInternalServiceCaller())
        {
            return;
        }

        var targetRoles = TenantWrightIdentityRoleNames.NormalizeMany(await _userManager.GetRolesAsync(user));
        if (targetRoles.Any(TenantWrightIdentityRoleNames.IsHostAdminRole))
        {
            throw new UnauthorizedAccessException("Tenant-scoped user management cannot target host admins.");
        }

        if (!IsCurrentUserHostAdmin() && targetRoles.Any(TenantWrightIdentityRoleNames.IsTenantAdminRole))
        {
            throw new UnauthorizedAccessException("Tenant-admins cannot manage peer or elevated tenant users.");
        }
    }

    public string NormalizeSingleRole(string roleName)
    {
        var normalized = TenantWrightIdentityRoleNames.Normalize(roleName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new SharedValidationException("Role name is required.");
        }

        return normalized;
    }
}
