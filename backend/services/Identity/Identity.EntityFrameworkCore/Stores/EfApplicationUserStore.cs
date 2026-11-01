using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.MultiTenancy;

namespace Identity.EntityFrameworkCore.Stores;

public class EfApplicationUserStore : UserStore<
    ApplicationUser,
    ApplicationRole,
    IdentityDbContextApp,
    Guid,
    IdentityUserClaim<Guid>,
    IdentityUserRole<Guid>,
    IdentityUserLogin<Guid>,
    IdentityUserToken<Guid>,
    IdentityRoleClaim<Guid>>,
    IQueryableUserStore<ApplicationUser>,
    IUserPasswordStore<ApplicationUser>,
    IUserEmailStore<ApplicationUser>,
    IUserPhoneNumberStore<ApplicationUser>,
    IUserRoleStore<ApplicationUser>,
    IUserSecurityStampStore<ApplicationUser>,
    IUserLockoutStore<ApplicationUser>,
    IUserTwoFactorStore<ApplicationUser>
{
    private readonly ICurrentTenant _currentTenant;

    public EfApplicationUserStore(
        IdentityDbContextApp context,
        ICurrentTenant currentTenant,
        IdentityErrorDescriber? describer = null)
        : base(context, describer)
    {
        _currentTenant = currentTenant;
    }

    protected override Task<ApplicationRole?> FindRoleAsync(string normalizedRoleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRoleName);

        var roles = Context.Set<ApplicationRole>();

        if (_currentTenant.Id.HasValue)
        {
            return roles.FirstOrDefaultAsync(
                role => role.TenantId == _currentTenant.Id && role.NormalizedName == normalizedRoleName,
                cancellationToken);
        }

        return roles.FirstOrDefaultAsync(
            role => role.TenantId == null && role.NormalizedName == normalizedRoleName,
            cancellationToken);
    }

    public override async Task<IList<string>> GetRolesAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);

        var roleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var roleName in await base.GetRolesAsync(user, cancellationToken))
        {
            roleNames.Add(roleName);
        }

        var organizationUnitRoleNames = await (
            from userOrganizationUnit in Context.Set<UserOrganizationUnit>()
            join organizationUnitRole in Context.Set<OrganizationUnitRole>()
                on userOrganizationUnit.OrganizationUnitId equals organizationUnitRole.OrganizationUnitId
            join role in Context.Set<ApplicationRole>()
                on organizationUnitRole.RoleId equals role.Id
            where userOrganizationUnit.UserId == user.Id
                && userOrganizationUnit.TenantId == user.TenantId
            select role.Name
        ).Where(x => x != null).Distinct().ToListAsync(cancellationToken);

        foreach (var roleName in organizationUnitRoleNames)
        {
            roleNames.Add(roleName!);
        }

        return roleNames.ToList();
    }

    public override async Task<bool> IsInRoleAsync(
        ApplicationUser user,
        string normalizedRoleName,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRoleName);

        var assignedRoles = await GetRolesAsync(user, cancellationToken);
        return assignedRoles.Any(role => string.Equals(role, normalizedRoleName, StringComparison.OrdinalIgnoreCase));
    }
}
