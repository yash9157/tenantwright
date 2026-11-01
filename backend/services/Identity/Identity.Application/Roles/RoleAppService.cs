using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.Application.Contracts.Roles;
using Identity.Application.Contracts.Caching;
using Identity.Application.ApplicationUsers;
using Identity.Application.Caching;
using Identity.Application.Tenancy;
using Identity.Application.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shared;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Roles;

public class RoleAppService : IRoleAppService, ITransientDependency
{
    private readonly ApplicationRoleManager _roleManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly IIdentityAuthCacheInvalidator _cacheInvalidator;
    private readonly TenantUserManagementGuard _managementGuard;

    public RoleAppService(
        ApplicationRoleManager roleManager, ICurrentTenant currentTenant, IDbContextProvider<IdentityDbContextApp> dbContextProvider, IIdentityAuthCacheInvalidator cacheInvalidator, TenantUserManagementGuard managementGuard)
    {
        _roleManager = roleManager;
        _currentTenant = currentTenant;
        _dbContextProvider = dbContextProvider;
        _cacheInvalidator = cacheInvalidator;
        _managementGuard = managementGuard;
    }

    public async Task<RoleDto> GetAsync(string id)
    {
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _currentTenant.RequireTenantId();
        var role = await FindTenantRoleAsync(id, tenantId);
        if (role == null) throw new EntityNotFoundException<ApplicationRole>(id);

        return MapToDto(role);
    }

    public async Task<List<RoleDto>> GetListAsync()
    {
        _managementGuard.EnsureUserManagementActor();
        var tenantId = _currentTenant.RequireTenantId();
        var roles = await _roleManager.Roles
            .Where(x => x.TenantId == tenantId)
            .ToListAsync();

        return roles.Select(MapToDto).ToList();
    }


    [Audited]
    public async Task<RoleDto> CreateAsync(RoleCreateDto input)
    {
        var tenantId = _currentTenant.RequireTenantId();
        EnsureCustomTenantRoleName(input.Name);

        var role = new ApplicationRole(input.Name);
        role.TenantId = tenantId;
        ApplyMetadata(role, input.Description, input.Category, input.IsSensitive);
        var result = await _roleManager.CreateAsync(role);
        if (!result.Succeeded) throw new SharedValidationException(string.Join(", ", result.Errors.Select(e => e.Description)));

        return MapToDto(role);
    }


    [Audited]
    public async Task<RoleDto> UpdateAsync(string id, RoleUpdateDto input)
    {
        var tenantId = _currentTenant.RequireTenantId();
        var role = await FindTenantRoleAsync(id, tenantId);
        if (role == null) throw new EntityNotFoundException<ApplicationRole>(id);
        EnsureMutableTenantRole(role, tenantId);
        EnsureCustomTenantRoleName(input.Name);
        var affectedUsers = await GetUsersByRoleIdAsync(role.Id, tenantId);

        role.Rename(input.Name);
        ApplyMetadata(role, input.Description, input.Category, input.IsSensitive);
        var result = await _roleManager.UpdateAsync(role);
        if (!result.Succeeded) throw new SharedValidationException(string.Join(", ", result.Errors.Select(e => e.Description)));

        foreach (var user in affectedUsers)
        {
            await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
        }

        return MapToDto(role);
    }


    [Audited]
    public async Task DeleteAsync(string id)
    {
        var tenantId = _currentTenant.RequireTenantId();
        var role = await FindTenantRoleAsync(id, tenantId);
        if (role == null) return;
        EnsureMutableTenantRole(role, tenantId);
        var affectedUsers = await GetUsersByRoleIdAsync(role.Id, tenantId);

        await _roleManager.DeleteAsync(role);

        foreach (var user in affectedUsers)
        {
            await _cacheInvalidator.InvalidateAuthorizationAsync(user.TenantId, user.Id);
        }
    }

    private async Task<ApplicationRole?> FindTenantRoleAsync(string id, Guid tenantId)
    {
        if (!Guid.TryParse(id, out var parsedId))
        {
            return null;
        }

        return await _roleManager.Roles
            .FirstOrDefaultAsync(x => x.Id == parsedId && x.TenantId == tenantId);
    }

    private static void EnsureCustomTenantRoleName(string? roleName)
    {
        if (TenantWrightIdentityRoleNames.IsTenantAdminRole(roleName))
        {
            throw new SharedValidationException("The TenantAdmin system role is managed by Identity provisioning.");
        }
    }

    private static void EnsureMutableTenantRole(ApplicationRole role, Guid tenantId)
    {
        if (role.Id == IdentitySystemRoleIds.TenantAdmin(tenantId) ||
            TenantWrightIdentityRoleNames.IsTenantAdminRole(role.Name))
        {
            throw new SharedValidationException("The TenantAdmin system role cannot be renamed or deleted.");
        }
    }

    private RoleDto MapToDto(ApplicationRole role)
    {
        return new RoleDto
        {
            Id = role.Id.ToString(),
            Name = role.Name!,
            Description = role.Description,
            Category = role.Category,
            IsSensitive = role.IsSensitive,
            IsStatic = TenantWrightIdentityRoleNames.IsTenantAdminRole(role.Name)
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

    private async Task<List<ApplicationUser>> GetUsersByRoleIdAsync(Guid roleId, Guid tenantId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var userIds = await unitOfWorkDbContext.UserRoles
                    .Where(x => x.RoleId == roleId)
                    .Select(x => x.UserId)
                    .Distinct()
                    .ToListAsync();

        if (userIds.Count == 0)
        {
            return new List<ApplicationUser>();
        }

        return await unitOfWorkDbContext.Users
            .Where(x => userIds.Contains(x.Id) && x.TenantId == tenantId)
            .ToListAsync();
    }
}
