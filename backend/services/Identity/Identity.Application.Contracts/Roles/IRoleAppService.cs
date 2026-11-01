using System.Collections.Generic;
using System.Threading.Tasks;

namespace Identity.Application.Contracts.Roles;

public class RoleDto
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public bool IsStatic { get; set; }
    public bool IsPublic { get; set; }

    // Additive role metadata for the roles UI. IsSensitive is an annotation flag
    // only (display/labelling); it does not yet gate assignment or approval.
    public string? Description { get; set; }
    public string? Category { get; set; }
    public bool IsSensitive { get; set; }
}

public class RoleCreateDto
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public bool IsSensitive { get; set; }
}

public class RoleUpdateDto
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public bool IsSensitive { get; set; }
}

public interface IRoleAppService
{
    Task<RoleDto> GetAsync(string id);
    Task<List<RoleDto>> GetListAsync();
    Task<RoleDto> CreateAsync(RoleCreateDto input);
    Task<RoleDto> UpdateAsync(string id, RoleUpdateDto input);
    Task DeleteAsync(string id);
}
