using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.MultiTenancy;

namespace Identity.EntityFrameworkCore.Stores;

public class EfApplicationRoleStore : RoleStore<
    ApplicationRole,
    IdentityDbContextApp,
    Guid,
    IdentityUserRole<Guid>,
    IdentityRoleClaim<Guid>>,
    IQueryableRoleStore<ApplicationRole>,
    IRoleClaimStore<ApplicationRole>
{
    private readonly ICurrentTenant _currentTenant;

    public EfApplicationRoleStore(
        IdentityDbContextApp context,
        ICurrentTenant currentTenant,
        IdentityErrorDescriber? describer = null)
        : base(context, describer)
    {
        _currentTenant = currentTenant;
    }

    public override Task<ApplicationRole?> FindByNameAsync(string normalizedName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedName);

        if (_currentTenant.Id.HasValue)
        {
            return Roles.FirstOrDefaultAsync(
                x => x.TenantId == _currentTenant.Id && x.NormalizedName == normalizedName,
                cancellationToken);
        }

        return Roles.FirstOrDefaultAsync(
            x => x.TenantId == null && x.NormalizedName == normalizedName,
            cancellationToken);
    }
}
