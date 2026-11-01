using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Identity.Application.Contracts.OrganizationUnits;

public class OrganizationUnitDto
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Code { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
}

public class OrganizationUnitCreateDto
{
    public Guid? ParentId { get; set; }
    public string DisplayName { get; set; } = default!;
}

public class OrganizationUnitUpdateDto
{
    public string DisplayName { get; set; } = default!;
}

public class OrganizationUnitUserDto
{
    public string Id { get; set; } = default!;
    public string UserName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public bool IsActive { get; set; }
}

public class OrganizationUnitRoleDto
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public bool IsStatic { get; set; }
    public bool IsPublic { get; set; }
}

public interface IOrganizationUnitAppService
{
    Task<OrganizationUnitDto> GetAsync(Guid id);
    Task<List<OrganizationUnitDto>> GetListAsync();
    Task<List<OrganizationUnitUserDto>> GetUsersAsync(Guid id);
    Task<List<OrganizationUnitRoleDto>> GetRolesAsync(Guid id);
    Task<OrganizationUnitDto> CreateAsync(OrganizationUnitCreateDto input);
    Task<OrganizationUnitDto> UpdateAsync(Guid id, OrganizationUnitUpdateDto input);
    Task DeleteAsync(Guid id);
    Task AddRoleAsync(Guid id, string roleId);
    Task AddUserAsync(Guid id, string userId);
    Task RemoveRoleAsync(Guid id, string roleId);
    Task RemoveUserAsync(Guid id, string userId);
}
