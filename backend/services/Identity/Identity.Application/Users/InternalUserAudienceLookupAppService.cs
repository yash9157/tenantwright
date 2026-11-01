using Identity.Application.Contracts.Users;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Users;

/// <summary>
/// Resolves audiences (users by role, or all tenant users) for the current tenant. Active users
/// only, ordered and paged. Returns an empty page when no tenant is in scope (fail-safe).
/// </summary>
public sealed class InternalUserAudienceLookupAppService : IInternalUserAudienceLookupAppService, ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;

    public InternalUserAudienceLookupAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
    }

    public async Task<UserAudiencePageDto> GetUsersByRoleAsync(string roleName, int skipCount, int maxResultCount)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var normalized = roleName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new SharedValidationException("Role name is required.");
        }

        if (!_currentTenant.Id.HasValue)
        {
            return new UserAudiencePageDto();
        }

        var tenantId = _currentTenant.Id.Value;
        var normalizedName = normalized.ToUpperInvariant();
        var skip = Math.Max(0, skipCount);
        var take = Math.Clamp(maxResultCount, 1, 500);

        var query =
            from ur in unitOfWorkDbContext.UserRoles
            join r in unitOfWorkDbContext.Roles on ur.RoleId equals r.Id
            join u in unitOfWorkDbContext.Users on ur.UserId equals u.Id
            where r.NormalizedName == normalizedName &&
                  u.TenantId == tenantId &&
                  u.IsActive &&
                  u.LifecycleStatus == IdentityUserLifecycleStatus.Active
            select u.Id;

        var totalCount = await query.CountAsync();
        var userIds = await query.OrderBy(x => x).Skip(skip).Take(take).ToListAsync();
        return new UserAudiencePageDto { UserIds = userIds, TotalCount = totalCount };
    }

    public async Task<UserAudiencePageDto> GetTenantUserIdsAsync(int skipCount, int maxResultCount)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (!_currentTenant.Id.HasValue)
        {
            return new UserAudiencePageDto();
        }

        var tenantId = _currentTenant.Id.Value;
        var skip = Math.Max(0, skipCount);
        var take = Math.Clamp(maxResultCount, 1, 500);

        var query = unitOfWorkDbContext.Users
            .Where(u =>
                u.TenantId == tenantId &&
                u.IsActive &&
                u.LifecycleStatus == IdentityUserLifecycleStatus.Active)
            .Select(u => u.Id);

        var totalCount = await query.CountAsync();
        var userIds = await query.OrderBy(x => x).Skip(skip).Take(take).ToListAsync();
        return new UserAudiencePageDto { UserIds = userIds, TotalCount = totalCount };
    }
}
