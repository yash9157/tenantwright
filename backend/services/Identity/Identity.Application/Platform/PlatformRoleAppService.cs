using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Caching;
using Identity.Application.Contracts.Platform;
using Identity.Application.Contracts.Users;
using Identity.Domain.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.CurrentUser;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.Application.Users;
using Microsoft.Extensions.Options;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Platform;

internal class PlatformRoleAppService : IPlatformRoleAppService, ITransientDependency
{
    private readonly ApplicationRoleManager _roleManager;
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly IIdentityAuthCacheInvalidator _cacheInvalidator;
    private readonly IIdentitySessionRevocationService _sessionRevocationService;
    private readonly ITenantWrightMessageSender _eventBus;
    private readonly RoleMembershipPolicyOptions _roleMembershipPolicy;

    public PlatformRoleAppService(
        ApplicationRoleManager roleManager, IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant, ICurrentUser currentUser, IIdentityAuthCacheInvalidator cacheInvalidator, IIdentitySessionRevocationService sessionRevocationService, ITenantWrightMessageSender eventBus, IOptions<RoleMembershipPolicyOptions> roleMembershipPolicy)
    {
        _roleManager = roleManager;
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _cacheInvalidator = cacheInvalidator;
        _sessionRevocationService = sessionRevocationService;
        _eventBus = eventBus;
        _roleMembershipPolicy = roleMembershipPolicy.Value;
    }

    public async Task<PlatformRoleDto> GetAsync(string id)
    {
        EnsurePlatformActor();
        var role = await FindPlatformRoleAsync(id);
        if (role == null)
        {
            throw new EntityNotFoundException<ApplicationRole>(id);
        }

        return MapToDto(role);
    }

    public async Task<PagedResultDto<PlatformRoleDto>> GetListAsync(PlatformRoleListInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsurePlatformActor();

        var query = unitOfWorkDbContext.Roles
            .AsNoTracking()
            .Where(x => x.TenantId == null);

        var filter = input.Filter?.Trim();
        if (!string.IsNullOrEmpty(filter))
        {
            var pattern = $"%{filter}%";
            query = query.Where(x =>
                (x.Name != null && EF.Functions.ILike(x.Name, pattern)) ||
                (x.Category != null && EF.Functions.ILike(x.Category, pattern)) ||
                (x.Description != null && EF.Functions.ILike(x.Description, pattern)));
        }

        var totalCount = await query.LongCountAsync();
        var skipCount = Math.Max(0, input.SkipCount);
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);

        // Rejects an undeclared field instead of silently falling back to the default order.
        var roles = await ApplySorting(query, SortingValidator.ValidateAndNormalize<PlatformRoleListInput>(input.Sorting))
            .Skip(skipCount)
            .Take(maxResultCount)
            .ToListAsync();

        var roleIds = roles.Select(role => role.Id).ToList();
        var memberCounts = await (
                from userRole in unitOfWorkDbContext.UserRoles
                join user in unitOfWorkDbContext.Users on userRole.UserId equals user.Id
                where roleIds.Contains(userRole.RoleId) && user.TenantId == Guid.Empty
                select userRole)
            .GroupBy(userRole => userRole.RoleId)
            .Select(group => new { RoleId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.RoleId, item => item.Count);

        return new PagedResultDto<PlatformRoleDto>
        {
            TotalCount = totalCount,
            Items = roles.Select(role => MapToDto(role, memberCounts.GetValueOrDefault(role.Id))).ToList()
        };
    }

    /// <summary>
    /// Maps a validated sort to an ordering. <c>SortableFieldsAttribute</c> on
    /// <see cref="PlatformRoleListInput"/> is the whitelist, so this mapping must stay complete for every
    /// declared field — a declared-but-unmapped field would fall through to the default and answer a
    /// different question than the caller asked.
    /// </summary>
    private static IQueryable<ApplicationRole> ApplySorting(IQueryable<ApplicationRole> query, string? sorting)
    {
        // Each branch ends with Id so offset paging is deterministic; without a total order a page can
        // repeat or drop rows. A bare field name means ascending.
        return sorting?.Trim().ToLowerInvariant() switch
        {
            "category" or "category asc" =>
                query.OrderBy(x => x.Category).ThenBy(x => x.Name).ThenBy(x => x.Id),
            "category desc" =>
                query.OrderByDescending(x => x.Category).ThenBy(x => x.Name).ThenBy(x => x.Id),
            "lifecyclestate" or "lifecyclestate asc" =>
                query.OrderBy(x => x.LifecycleState).ThenBy(x => x.Name).ThenBy(x => x.Id),
            "lifecyclestate desc" =>
                query.OrderByDescending(x => x.LifecycleState).ThenBy(x => x.Name).ThenBy(x => x.Id),
            "name desc" => query.OrderByDescending(x => x.Name).ThenBy(x => x.Id),
            _ => query.OrderBy(x => x.Name).ThenBy(x => x.Id)
        };
    }

    public async Task<PagedResultDto<PlatformRoleMemberDto>> GetMembersAsync(string id, PagedRequestDto input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsurePlatformActor();
        var role = await FindPlatformRoleAsync(id) ?? throw new EntityNotFoundException<ApplicationRole>(id);
        var query = from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
                    join user in unitOfWorkDbContext.Users.AsNoTracking() on userRole.UserId equals user.Id
                    where userRole.RoleId == role.Id && user.TenantId == Guid.Empty
                    orderby user.UserName
                    select new PlatformRoleMemberDto
                    {
                        Id = user.Id.ToString("D"),
                        UserName = user.UserName!,
                        Email = user.Email!,
                        Name = user.Name,
                        Surname = user.Surname,
                        IsActive = user.IsActive
                    };

        var totalCount = await query.LongCountAsync();
        var items = await query
            .Skip(Math.Max(input.SkipCount, 0))
            .Take(Math.Clamp(input.MaxResultCount, 1, 100))
            .ToListAsync();
        return new PagedResultDto<PlatformRoleMemberDto> { TotalCount = totalCount, Items = items };
    }

    public async Task<RoleMembershipCardinalityReportDto> GetMembershipCardinalityReportAsync()
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsurePlatformActor();
        var maximum = _roleMembershipPolicy.MaxActiveRoleMemberships;
        var violations = await (
                from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
                join user in unitOfWorkDbContext.Users.AsNoTracking() on userRole.UserId equals user.Id
                join role in unitOfWorkDbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                where user.TenantId == Guid.Empty &&
                      role.TenantId == null &&
                      role.LifecycleState == ApplicationRoleLifecycleState.Active
                group role by new { user.Id, user.UserName } into membership
                where membership.Count() > maximum
                orderby membership.Count() descending, membership.Key.UserName
                select new RoleMembershipCardinalityReportItemDto
                {
                    UserId = membership.Key.Id,
                    UserName = membership.Key.UserName!,
                    ActiveRoleCount = membership.Count()
                })
            .ToListAsync();

        return new RoleMembershipCardinalityReportDto
        {
            SupportedMaximum = maximum,
            Violations = violations
        };
    }

    [Audited]
    public async Task<PlatformRoleDto> CreateAsync(PlatformRoleCreateDto input)
    {
        EnsurePlatformActor();
        var roleName = NormalizeAllowedPlatformRole(input.Name);
        var role = new ApplicationRole(roleName)
        {
            TenantId = null
        };
        ApplyMetadata(role, input.Description, input.Category, input.IsSensitive);

        var result = await _roleManager.CreateAsync(role);
        if (!result.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        return MapToDto(role);
    }

    [Audited]
    public async Task<PlatformRoleDto> UpdateAsync(string id, PlatformRoleUpdateDto input)
    {
        EnsurePlatformActor();
        var role = await FindPlatformRoleAsync(id);
        if (role == null)
        {
            throw new EntityNotFoundException<ApplicationRole>(id);
        }

        EnsureMutablePlatformRole(role.Name);
        var affectedUsers = await GetUsersByRoleIdAsync(role.Id);
        role.Rename(NormalizeAllowedPlatformRole(input.Name));
        ApplyMetadata(role, input.Description, input.Category, input.IsSensitive);
        var result = await _roleManager.UpdateAsync(role);
        if (!result.Succeeded)
        {
            throw new SharedValidationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        foreach (var user in affectedUsers)
        {
            await _cacheInvalidator.InvalidateAuthorizationAsync(null, user.Id);
            await _sessionRevocationService.RevokeAllAsync(
                user.Id,
                tenantId: null,
                action: "PlatformRoleRenamed");
        }

        return MapToDto(role);
    }

    [Audited]
    public async Task<PlatformRoleDto> BeginRetirementAsync(string id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        EnsurePlatformActor();
        var role = await FindPlatformRoleAsync(id)
                   ?? throw new EntityNotFoundException<ApplicationRole>(id);
        EnsureMutablePlatformRole(role.Name);

        if (role.LifecycleState == ApplicationRoleLifecycleState.Retiring)
        {
            return MapToDto(role);
        }

        var affectedUsers = await GetUsersByRoleIdAsync(role.Id);
        var requestId = Guid.CreateVersion7();
        role.BeginRetirement(requestId, DateTime.UtcNow);

        var memberships = await unitOfWorkDbContext.UserRoles
            .Where(x => x.RoleId == role.Id)
            .ToListAsync();
        unitOfWorkDbContext.UserRoles.RemoveRange(memberships);
        await unitOfWorkDbContext.SaveChangesAsync();

        foreach (var user in affectedUsers)
        {
            await _cacheInvalidator.InvalidateAuthorizationAsync(null, user.Id);
            await _sessionRevocationService.RevokeAllAsync(
                user.Id,
                tenantId: null,
                action: "PlatformRoleRetiring");
        }

        await _eventBus.PublishEventAsync(
            new IdentityPlatformRoleRetirementRequestedEto
            {
                RequestId = requestId,
                RoleId = role.Id,
                LifecycleVersion = role.LifecycleVersion,
                OccurredAtUtc = DateTime.UtcNow
            });

        return MapToDto(role);
    }

    [Audited]
    public async Task DeleteAsync(string id)
    {
        EnsurePlatformActor();
        var role = await FindPlatformRoleAsync(id);
        if (role == null)
        {
            return;
        }

        EnsureMutablePlatformRole(role.Name);
        throw new SharedConflictException(
            "Platform roles must be retired through the retirement endpoint before deletion.");
    }

    private void EnsurePlatformActor()
    {
        if (!_currentUser.IsAuthenticated ||
            (!_currentUser.IsInRole(TenantWrightIdentityRoleNames.HostAdmin) &&
             !_currentUser.IsInRole("HostAdmin")))
        {
            throw new UnauthorizedAccessException("Platform role management requires host admin context.");
        }

        if (_currentTenant.Id.HasValue)
        {
            throw new UnauthorizedAccessException("Platform role management is only available outside tenant context.");
        }
    }

    private async Task<ApplicationRole?> FindPlatformRoleAsync(string id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (!Guid.TryParse(id, out var parsedId))
        {
            return null;
        }

        return await unitOfWorkDbContext.Roles.FirstOrDefaultAsync(x => x.Id == parsedId && x.TenantId == null);
    }

    private async Task<List<ApplicationUser>> GetUsersByRoleIdAsync(Guid roleId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userIds = await unitOfWorkDbContext.UserRoles
                    .Where(x => x.RoleId == roleId)
                    .Select(x => x.UserId)
                    .Distinct()
                    .ToListAsync();

        if (userIds.Count == 0)
        {
            return [];
        }

        return await unitOfWorkDbContext.Users
            .Where(x => userIds.Contains(x.Id) && x.TenantId == Guid.Empty)
            .ToListAsync();
    }

    private static PlatformRoleDto MapToDto(ApplicationRole role, int memberCount = 0)
    {
        return new PlatformRoleDto
        {
            Id = role.Id.ToString("D"),
            Name = role.Name!,
            IsStatic = IsReservedPlatformRole(role.Name),
            IsPublic = false,
            Description = role.Description,
            Category = role.Category,
            IsSensitive = role.IsSensitive,
            MemberCount = memberCount,
            LifecycleState = role.LifecycleState,
            LifecycleVersion = role.LifecycleVersion,
            RetirementRequestId = role.RetirementRequestId,
            RetirementRequestedAtUtc = role.RetirementRequestedAtUtc
        };
    }

    private static void ApplyMetadata(ApplicationRole role, string? description, string? category, bool isSensitive)
    {
        var normalizedDescription = description?.Trim();
        if (normalizedDescription is { Length: > 512 })
        {
            throw new SharedValidationException("Role description must be 512 characters or fewer.");
        }

        var normalizedCategory = category?.Trim();
        if (normalizedCategory is { Length: > 64 })
        {
            throw new SharedValidationException("Role category must be 64 characters or fewer.");
        }

        role.Description = string.IsNullOrWhiteSpace(normalizedDescription) ? null : normalizedDescription;
        role.Category = string.IsNullOrWhiteSpace(normalizedCategory) ? null : normalizedCategory;
        role.IsSensitive = isSensitive;
    }

    private static string NormalizeAllowedPlatformRole(string? roleName)
    {
        var normalized = TenantWrightIdentityRoleNames.Normalize(roleName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new SharedValidationException("Role name is required.");
        }

        EnsureMutablePlatformRole(normalized);
        return normalized;
    }

    private static void EnsureMutablePlatformRole(string? roleName)
    {
        if (IsReservedPlatformRole(roleName))
        {
            throw new UnauthorizedAccessException("Reserved admin roles cannot be managed by platform role APIs.");
        }
    }

    private static bool IsReservedPlatformRole(string? roleName)
    {
        return TenantWrightIdentityRoleNames.IsHostAdminRole(roleName) ||
               TenantWrightIdentityRoleNames.IsTenantAdminRole(roleName);
    }
}
