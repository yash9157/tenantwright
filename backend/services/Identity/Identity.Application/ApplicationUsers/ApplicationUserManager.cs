using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Identity.Domain.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Identity.Application.Users;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.EntityFrameworkCore;
namespace Identity.Application.ApplicationUsers;

public class ApplicationUserManager : UserManager<ApplicationUser>, IScopedDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly RoleMembershipPolicyOptions _roleMembershipPolicy;

    public ApplicationUserManager(
        IUserStore<ApplicationUser> store, IOptions<IdentityOptions> optionsAccessor, IPasswordHasher<ApplicationUser> passwordHasher, IEnumerable<IUserValidator<ApplicationUser>> userValidators, IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators, ILookupNormalizer keyNormalizer, IdentityErrorDescriber errors, IServiceProvider services, ILogger<ApplicationUserManager> logger, IDbContextProvider<IdentityDbContextApp> dbContextProvider, IOptions<RoleMembershipPolicyOptions> roleMembershipPolicy)
        : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
        _dbContextProvider = dbContextProvider;
        _roleMembershipPolicy = roleMembershipPolicy.Value;
    }

    public override Task<IdentityResult> AddToRoleAsync(ApplicationUser user, string role)
        => AddToRolesWithPolicyAsync(user, [role], () => base.AddToRoleAsync(user, role));

    public override Task<IdentityResult> AddToRolesAsync(ApplicationUser user, IEnumerable<string> roles)
    {
        var requestedRoles = roles.ToArray();
        return AddToRolesWithPolicyAsync(user, requestedRoles, () => base.AddToRolesAsync(user, requestedRoles));
    }

    private async Task<IdentityResult> AddToRolesWithPolicyAsync(
        ApplicationUser user,
        string[] requestedRoleNames,
        Func<Task<IdentityResult>> mutation)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (requestedRoleNames.Length == 0)
        {
            return IdentityResult.Success;
        }

        IDbContextTransaction? ownedTransaction = null;
        try
        {
            if (unitOfWorkDbContext.Database.IsRelational())
            {
                if (unitOfWorkDbContext.Database.CurrentTransaction == null)
                {
                    ownedTransaction = await unitOfWorkDbContext.Database.BeginTransactionAsync();
                }

                await unitOfWorkDbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = {user.Id} FOR UPDATE");
            }

            var normalizedNames = requestedRoleNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var roleTenantId = user.TenantId == Guid.Empty ? (Guid?)null : user.TenantId;

            var requestedRoles = await unitOfWorkDbContext.Roles
                .Where(x => x.TenantId == roleTenantId && normalizedNames.Contains(x.NormalizedName!))
                .Select(x => new { x.Id, x.LifecycleState })
                .ToListAsync();
            if (requestedRoles.Count != normalizedNames.Length ||
                requestedRoles.Any(x => x.LifecycleState != ApplicationRoleLifecycleState.Active))
            {
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "role_not_active",
                    Description = "One or more selected roles are unavailable for assignment."
                });
            }

            var currentActiveRoleIds = await (
                    from userRole in unitOfWorkDbContext.UserRoles
                    join role in unitOfWorkDbContext.Roles on userRole.RoleId equals role.Id
                    where userRole.UserId == user.Id &&
                          role.TenantId == roleTenantId &&
                          role.LifecycleState == ApplicationRoleLifecycleState.Active
                    select role.Id)
                .Distinct()
                .ToListAsync();
            var projectedCount = currentActiveRoleIds.Union(requestedRoles.Select(x => x.Id)).Count();
            if (projectedCount > _roleMembershipPolicy.MaxActiveRoleMemberships)
            {
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "active_role_membership_limit_exceeded",
                    Description = $"A user cannot have more than {_roleMembershipPolicy.MaxActiveRoleMemberships} active roles."
                });
            }

            var result = await mutation();
            if (result.Succeeded && ownedTransaction != null)
            {
                await ownedTransaction.CommitAsync();
            }

            return result;
        }
        catch
        {
            if (ownedTransaction != null)
            {
                await ownedTransaction.RollbackAsync();
            }

            throw;
        }
        finally
        {
            if (ownedTransaction != null)
            {
                await ownedTransaction.DisposeAsync();
            }
        }
    }

    public virtual async Task<ApplicationUser> GetByIdAsync(Guid id)
    {
        var user = await FindByIdAsync(id.ToString());
        if (user == null) throw new EntityNotFoundException<ApplicationUser>(id);
        return user;
    }

    public virtual async Task AddToOrganizationUnitAsync(Guid userId, Guid ouId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var user = await GetByIdAsync(userId);
        if (await IsInOrganizationUnitAsync(userId, ouId)) return;

        var userOu = new UserOrganizationUnit
        {
            UserId = userId,
            OrganizationUnitId = ouId,
            TenantId = user.TenantId
        };

        await unitOfWorkDbContext.AddAsync(userOu);
        await unitOfWorkDbContext.SaveChangesAsync();
    }

    public virtual async Task RemoveFromOrganizationUnitAsync(Guid userId, Guid ouId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userOu = await unitOfWorkDbContext.UserOrganizationUnits
                    .FirstOrDefaultAsync(x => x.UserId == userId && x.OrganizationUnitId == ouId);

        if (userOu != null)
        {
            unitOfWorkDbContext.Remove(userOu);
            await unitOfWorkDbContext.SaveChangesAsync();
        }
    }

    public virtual async Task<bool> IsInOrganizationUnitAsync(Guid userId, Guid ouId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        return await unitOfWorkDbContext.UserOrganizationUnits.AnyAsync(x => x.UserId == userId && x.OrganizationUnitId == ouId);
    }

    public virtual async Task<List<OrganizationUnit>> GetOrganizationUnitsAsync(ApplicationUser user)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var ouIds = await unitOfWorkDbContext.UserOrganizationUnits
                    .Where(x => x.UserId == user.Id)
                    .Select(x => x.OrganizationUnitId)
                    .ToListAsync();

        return await unitOfWorkDbContext.OrganizationUnits
            .Where(x => ouIds.Contains(x.Id))
            .ToListAsync();
    }
}
