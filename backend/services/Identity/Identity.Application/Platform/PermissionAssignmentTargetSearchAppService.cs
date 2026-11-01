using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Platform;
using Identity.Application.Contracts.Users;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.CurrentUser;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Platform;

internal sealed class PermissionAssignmentTargetSearchAppService
    : IPermissionAssignmentTargetSearchAppService, ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;

    public PermissionAssignmentTargetSearchAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant, ICurrentUser currentUser)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
    }

    public async Task<PagedResultDto<PermissionAssignmentUserTargetDto>> GetUsersAsync(
        PermissionAssignmentTargetSearchInput input,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostActor();
        input ??= new PermissionAssignmentTargetSearchInput();
        EnsureTenantScope(input.TenantId);
        var filter = input.Filter?.Trim();
        var userTenantId = input.TenantId ?? Guid.Empty;
        var users = unitOfWorkDbContext.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == userTenantId);
        if (input.TenantId.HasValue)
        {
            users = users.Where(x => x.IsActive &&
                                     (x.LockoutEnd == null || x.LockoutEnd <= DateTimeOffset.UtcNow));
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var pattern = $"%{filter}%";
            users = users.Where(x =>
                EF.Functions.ILike(x.UserName!, pattern) ||
                EF.Functions.ILike(x.Email!, pattern) ||
                (x.Name != null && EF.Functions.ILike(x.Name, pattern)) ||
                (x.Surname != null && EF.Functions.ILike(x.Surname, pattern)));
        }

        var totalCount = await users.LongCountAsync(cancellationToken);
        var page = await users
            .OrderBy(x => x.UserName).ThenBy(x => x.Id)
            .Skip(Math.Max(input.SkipCount, 0))
            .Take(Math.Clamp(input.MaxResultCount, 1, 50))
            .Select(x => new PermissionAssignmentUserTargetDto
            {
                Id = x.Id,
                UserName = x.UserName!,
                Email = x.Email!,
                Name = x.Name,
                Surname = x.Surname,
                IsActive = x.IsActive
            })
            .ToListAsync(cancellationToken);

        var userIds = page.Select(x => x.Id).ToArray();
        var memberships = await (
                from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
                join role in unitOfWorkDbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                where userIds.Contains(userRole.UserId) &&
                      role.LifecycleState == ApplicationRoleLifecycleState.Active && role.Name != null
                select new { userRole.UserId, RoleName = role.Name! })
            .ToListAsync(cancellationToken);
        var roleNamesByUser = memberships
            .GroupBy(x => x.UserId)
            .ToDictionary(
                x => x.Key,
                x => x.Select(y => y.RoleName).Distinct(StringComparer.Ordinal).OrderBy(y => y, StringComparer.Ordinal).ToList());
        foreach (var user in page)
        {
            user.RoleNames = roleNamesByUser.GetValueOrDefault(user.Id) ?? [];
        }

        return new PagedResultDto<PermissionAssignmentUserTargetDto> { TotalCount = totalCount, Items = page };
    }

    public async Task<PagedResultDto<PermissionAssignmentRoleTargetDto>> GetRolesAsync(
        PermissionAssignmentTargetSearchInput input,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsureHostActor();
        input ??= new PermissionAssignmentTargetSearchInput();
        EnsureTenantScope(input.TenantId);
        var filter = input.Filter?.Trim();
        var roles = unitOfWorkDbContext.Roles.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == input.TenantId &&
                        x.LifecycleState == ApplicationRoleLifecycleState.Active);
        roles = input.TenantId.HasValue
            ? roles.Where(x => x.Name != TenantWrightIdentityRoleNames.TenantAdmin)
            : roles.Where(x => x.Name != TenantWrightIdentityRoleNames.HostAdmin &&
                               x.Name != TenantWrightIdentityRoleNames.TenantAdmin);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var pattern = $"%{filter}%";
            roles = roles.Where(x => EF.Functions.ILike(x.Name!, pattern) ||
                                     (x.Description != null && EF.Functions.ILike(x.Description, pattern)));
        }

        var totalCount = await roles.LongCountAsync(cancellationToken);
        var page = await roles
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Skip(Math.Max(input.SkipCount, 0))
            .Take(Math.Clamp(input.MaxResultCount, 1, 50))
            .Select(x => new PermissionAssignmentRoleTargetDto
            {
                Id = x.Id,
                Name = x.Name!,
                Description = x.Description,
                Category = x.Category,
                IsSensitive = x.IsSensitive,
                LifecycleState = x.LifecycleState,
                LifecycleVersion = x.LifecycleVersion
            })
            .ToListAsync(cancellationToken);
        return new PagedResultDto<PermissionAssignmentRoleTargetDto> { TotalCount = totalCount, Items = page };
    }

    private void EnsureHostActor()
    {
        if (_currentTenant.Id.HasValue || !_currentUser.IsAuthenticated ||
            (!_currentUser.IsInRole("admin") && !_currentUser.IsInRole("HostAdmin")))
        {
            throw new UnauthorizedAccessException("Permission assignment target search requires host administrator context.");
        }
    }

    private static void EnsureTenantScope(Guid? tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant scope must be a non-empty identifier.", nameof(tenantId));
        }
    }
}
