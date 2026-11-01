using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.InternalPermissionTargets;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Identity.Domain.Shared.ApplicationUsers;
using Shared.EntityFrameworkCore;
namespace Identity.Application.InternalPermissionTargets;

internal sealed class IdentityPermissionTargetValidationAppService : IIdentityPermissionTargetValidationAppService, ITransientDependency
{
    private static readonly Guid HostUserTenantId = Guid.Empty;

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;

    public IdentityPermissionTargetValidationAppService(IDbContextProvider<IdentityDbContextApp> dbContextProvider)
    {
        _dbContextProvider = dbContextProvider;
    }

    public async Task<RolePermissionTargetDto?> GetRoleAsync(
        Guid roleId,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (roleId == Guid.Empty)
        {
            return null;
        }

        var role = await unitOfWorkDbContext.Roles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.Id == roleId && x.TenantId == tenantId)
            .Select(x => new { x.Id, x.Name, x.LifecycleState, x.LifecycleVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (role == null || string.IsNullOrWhiteSpace(role.Name))
        {
            return null;
        }

        return new RolePermissionTargetDto
        {
            Id = role.Id,
            Name = role.Name,
            IsStatic = IsReservedPlatformRole(role.Name),
            LifecycleState = role.LifecycleState,
            LifecycleVersion = role.LifecycleVersion
        };
    }

    public async Task<UserPermissionTargetDto?> GetUserAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (userId == Guid.Empty)
        {
            return null;
        }

        var requestedTenantId = tenantId ?? HostUserTenantId;
        var user = await unitOfWorkDbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.Id == userId && x.TenantId == requestedTenantId)
            .Select(x => new
            {
                x.Id,
                x.UserName,
                x.Name,
                x.Surname,
                x.IsActive,
                x.LifecycleStatus,
                x.LockoutEnabled,
                x.LockoutEnd
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (user == null || string.IsNullOrWhiteSpace(user.UserName))
        {
            return null;
        }

        var roles = await (
            from userRole in unitOfWorkDbContext.UserRoles.IgnoreQueryFilters().AsNoTracking()
            join role in unitOfWorkDbContext.Roles.IgnoreQueryFilters().AsNoTracking() on userRole.RoleId equals role.Id
            where userRole.UserId == userId && role.TenantId == tenantId && role.Name != null &&
                  role.LifecycleState == ApplicationRoleLifecycleState.Active
            select new { role.Id, Name = role.Name! })
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var fullName = string.Join(" ", new[] { user.Name, user.Surname }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new UserPermissionTargetDto
        {
            Id = userId.ToString("D"),
            UserName = user.UserName,
            DisplayName = string.IsNullOrWhiteSpace(fullName) ? user.UserName : fullName,
            IsActive = user.IsActive && user.LifecycleStatus == IdentityUserLifecycleStatus.Active,
            IsLockedOut = user.LockoutEnabled && user.LockoutEnd is { } lockoutEnd && lockoutEnd > now,
            RoleIds = roles.Select(x => x.Id).Distinct().OrderBy(x => x).ToList()
        };
    }

    public async Task<List<ResolvedPermissionTargetDto>> ResolveAsync(
        ResolvePermissionTargetsInput input,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (input?.Targets is not { Count: > 0 and <= 200 } ||
                    input.Targets.Any(target => target.Id == Guid.Empty))
        {
            throw new ArgumentException("Permission target resolution requires 1 to 200 valid targets.", nameof(input));
        }

        var roleTargets = input.Targets.Where(target => target.Kind == PermissionTargetKind.Role).ToList();
        var userTargets = input.Targets.Where(target => target.Kind == PermissionTargetKind.User).ToList();
        if (roleTargets.Count + userTargets.Count != input.Targets.Count)
        {
            throw new ArgumentException("Permission target type is invalid.", nameof(input));
        }

        var roleIds = roleTargets.Select(target => target.Id).Distinct().ToList();
        var roles = await unitOfWorkDbContext.Roles.IgnoreQueryFilters().AsNoTracking()
            .Where(role => roleIds.Contains(role.Id))
            .Select(role => new { role.Id, role.TenantId, role.Name })
            .ToListAsync(cancellationToken);
        var userIds = userTargets.Select(target => target.Id).Distinct().ToList();
        var users = await unitOfWorkDbContext.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.TenantId, user.UserName, user.Name, user.Surname })
            .ToListAsync(cancellationToken);

        var results = new List<ResolvedPermissionTargetDto>(input.Targets.Count);
        foreach (var target in input.Targets.DistinctBy(target => (target.Kind, target.Id, target.TenantId)))
        {
            if (target.Kind == PermissionTargetKind.Role)
            {
                var role = roles.SingleOrDefault(candidate => candidate.Id == target.Id && candidate.TenantId == target.TenantId);
                if (role?.Name != null)
                {
                    results.Add(new ResolvedPermissionTargetDto { Kind = target.Kind, Id = target.Id, TenantId = target.TenantId, DisplayName = role.Name });
                }
                continue;
            }

            var expectedTenantId = target.TenantId ?? HostUserTenantId;
            var user = users.SingleOrDefault(candidate => candidate.Id == target.Id && candidate.TenantId == expectedTenantId);
            if (user?.UserName != null)
            {
                var fullName = string.Join(" ", new[] { user.Name, user.Surname }.Where(value => !string.IsNullOrWhiteSpace(value)));
                results.Add(new ResolvedPermissionTargetDto
                {
                    Kind = target.Kind,
                    Id = target.Id,
                    TenantId = target.TenantId,
                    DisplayName = string.IsNullOrWhiteSpace(fullName) ? user.UserName : fullName
                });
            }
        }

        return results;
    }

    private static bool IsReservedPlatformRole(string? roleName)
    {
        return TenantWrightIdentityRoleNames.IsHostAdminRole(roleName) ||
               TenantWrightIdentityRoleNames.IsTenantAdminRole(roleName);
    }
}
