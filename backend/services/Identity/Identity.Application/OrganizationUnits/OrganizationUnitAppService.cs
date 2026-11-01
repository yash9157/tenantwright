using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Identity.Application.Contracts.OrganizationUnits;
using Identity.Application.Tenancy;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
namespace Identity.Application.OrganizationUnits;

public class OrganizationUnitAppService : IOrganizationUnitAppService, ITransientDependency
{
    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;

    public OrganizationUnitAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
    }

    public async Task<OrganizationUnitDto> GetAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var ou = await unitOfWorkDbContext.OrganizationUnits
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);

        if (ou == null) throw new EntityNotFoundException<OrganizationUnit>(id);

        return MapToDto(ou);
    }

    public async Task<List<OrganizationUnitDto>> GetListAsync()
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var list = await unitOfWorkDbContext.OrganizationUnits
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .ToListAsync();

        return list.Select(MapToDto).ToList();
    }

    public async Task<List<OrganizationUnitUserDto>> GetUsersAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        await EnsureOrganizationUnitAsync(id, tenantId);

        return await (
                from userOrganizationUnit in unitOfWorkDbContext.UserOrganizationUnits.AsNoTracking()
                join user in unitOfWorkDbContext.Users.AsNoTracking()
                    on userOrganizationUnit.UserId equals user.Id
                where userOrganizationUnit.OrganizationUnitId == id
                    && userOrganizationUnit.TenantId == tenantId
                    && user.TenantId == tenantId
                orderby user.UserName
                select new OrganizationUnitUserDto
                {
                    Id = user.Id.ToString(),
                    UserName = user.UserName ?? string.Empty,
                    Email = user.Email ?? string.Empty,
                    IsActive = user.IsActive && user.LifecycleStatus == IdentityUserLifecycleStatus.Active
                })
            .ToListAsync();
    }

    public async Task<List<OrganizationUnitRoleDto>> GetRolesAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        await EnsureOrganizationUnitAsync(id, tenantId);

        return await (
                from organizationUnitRole in unitOfWorkDbContext.OrganizationUnitRoles.AsNoTracking()
                join role in unitOfWorkDbContext.Roles.AsNoTracking()
                    on organizationUnitRole.RoleId equals role.Id
                where organizationUnitRole.OrganizationUnitId == id
                    && organizationUnitRole.TenantId == tenantId
                    && role.TenantId == tenantId
                orderby role.Name
                select new OrganizationUnitRoleDto
                {
                    Id = role.Id.ToString(),
                    Name = role.Name ?? string.Empty,
                    IsStatic = false,
                    IsPublic = true
                })
            .ToListAsync();
    }


    [Audited]
    public async Task<OrganizationUnitDto> CreateAsync(OrganizationUnitCreateDto input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();

        var ou = new OrganizationUnit
        {
            Id = Guid.CreateVersion7(),
            ParentId = input.ParentId,
            DisplayName = input.DisplayName,
            TenantId = tenantId,
            Code = await GetNextChildCodeAsync(input.ParentId)
        };

        await unitOfWorkDbContext.AddAsync(ou);
        await unitOfWorkDbContext.SaveChangesAsync();

        return MapToDto(ou);
    }


    [Audited]
    public async Task<OrganizationUnitDto> UpdateAsync(Guid id, OrganizationUnitUpdateDto input)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var ou = await unitOfWorkDbContext.OrganizationUnits.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (ou == null) throw new EntityNotFoundException<OrganizationUnit>(id);

        ou.DisplayName = input.DisplayName;
        await unitOfWorkDbContext.SaveChangesAsync();

        return MapToDto(ou);
    }


    [Audited]
    public async Task DeleteAsync(Guid id)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var ou = await unitOfWorkDbContext.OrganizationUnits.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (ou == null) return;

        unitOfWorkDbContext.Remove(ou);
        await unitOfWorkDbContext.SaveChangesAsync();
    }


    [Audited]
    public async Task AddRoleAsync(Guid id, string roleId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var ou = await unitOfWorkDbContext.OrganizationUnits
            .Include(x => x.Roles)
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);

        if (ou == null) throw new EntityNotFoundException<OrganizationUnit>(id);

        if (!Guid.TryParse(roleId, out var parsedRoleId))
        {
            throw new SharedValidationException("Invalid role id.");
        }

        if (!ou.Roles.Any(r => r.RoleId == parsedRoleId))
        {
            ou.Roles.Add(new OrganizationUnitRole { OrganizationUnitId = id, RoleId = parsedRoleId, TenantId = ou.TenantId });
            await unitOfWorkDbContext.SaveChangesAsync();
        }
    }

    [Audited]
    public async Task RemoveRoleAsync(Guid id, string roleId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        await EnsureOrganizationUnitAsync(id, tenantId);

        if (!Guid.TryParse(roleId, out var parsedRoleId))
        {
            throw new SharedValidationException("Invalid role id.");
        }

        var organizationUnitRole = await unitOfWorkDbContext.OrganizationUnitRoles.FirstOrDefaultAsync(x =>
            x.OrganizationUnitId == id &&
            x.RoleId == parsedRoleId &&
            x.TenantId == tenantId);

        if (organizationUnitRole == null)
        {
            return;
        }

        unitOfWorkDbContext.Remove(organizationUnitRole);
        await unitOfWorkDbContext.SaveChangesAsync();
    }


    [Audited]
    public async Task AddUserAsync(Guid id, string userId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var ou = await unitOfWorkDbContext.OrganizationUnits
            .Include(x => x.Users)
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);

        if (ou == null) throw new EntityNotFoundException<OrganizationUnit>(id);

        if (!Guid.TryParse(userId, out var parsedUserId))
        {
            throw new SharedValidationException("Invalid user id.");
        }

        if (!ou.Users.Any(u => u.UserId == parsedUserId))
        {
            ou.Users.Add(new UserOrganizationUnit { OrganizationUnitId = id, UserId = parsedUserId, TenantId = ou.TenantId });
            await unitOfWorkDbContext.SaveChangesAsync();
        }
    }

    [Audited]
    public async Task RemoveUserAsync(Guid id, string userId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        await EnsureOrganizationUnitAsync(id, tenantId);

        if (!Guid.TryParse(userId, out var parsedUserId))
        {
            throw new SharedValidationException("Invalid user id.");
        }

        var userOrganizationUnit = await unitOfWorkDbContext.UserOrganizationUnits.FirstOrDefaultAsync(x =>
            x.OrganizationUnitId == id &&
            x.UserId == parsedUserId &&
            x.TenantId == tenantId);

        if (userOrganizationUnit == null)
        {
            return;
        }

        unitOfWorkDbContext.Remove(userOrganizationUnit);
        await unitOfWorkDbContext.SaveChangesAsync();
    }

    private OrganizationUnitDto MapToDto(OrganizationUnit ou)
    {
        return new OrganizationUnitDto
        {
            Id = ou.Id,
            ParentId = ou.ParentId,
            Code = ou.Code,
            DisplayName = ou.DisplayName
        };
    }

    private async Task EnsureOrganizationUnitAsync(Guid id, Guid tenantId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var exists = await unitOfWorkDbContext.OrganizationUnits
                    .AsNoTracking()
                    .AnyAsync(x => x.Id == id && x.TenantId == tenantId);

        if (!exists)
        {
            throw new EntityNotFoundException<OrganizationUnit>(id);
        }
    }

    private async Task<string> GetNextChildCodeAsync(Guid? parentId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var tenantId = _currentTenant.RequireTenantId();
        var lastChild = await unitOfWorkDbContext.OrganizationUnits
            .Where(ou => ou.ParentId == parentId && ou.TenantId == tenantId)
            .OrderByDescending(ou => ou.Code)
            .FirstOrDefaultAsync();

        if (lastChild == null)
        {
            var parentCode = parentId.HasValue
                ? (await unitOfWorkDbContext.OrganizationUnits
                    .Where(x => x.Id == parentId.Value && x.TenantId == tenantId)
                    .Select(x => x.Code)
                    .FirstOrDefaultAsync())
                : null;
            return AppendCode(parentCode, CreateCode(1));
        }

        return CalculateNextCode(lastChild.Code);
    }

    private static string AppendCode(string? parentCode, string childCode)
    {
        if (string.IsNullOrEmpty(parentCode)) return childCode;
        return parentCode + "." + childCode;
    }

    private static string CreateCode(int number)
    {
        return number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5, '0');
    }

    private static string CalculateNextCode(string code)
    {
        var parts = code.Split('.');
        var lastPart = parts.Last();
        var nextNumber = int.Parse(lastPart, System.Globalization.CultureInfo.InvariantCulture) + 1;
        var nextCode = CreateCode(nextNumber);

        if (parts.Length == 1) return nextCode;
        return string.Join(".", parts.Take(parts.Length - 1)) + "." + nextCode;
    }
}
