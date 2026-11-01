using Permissions.Application.Contracts.PermissionManagement;

namespace Permissions.Application.Contracts.ResourcePermissionManagement;

public class UpdateResourcePermissionsDto
{
    public List<UpdatePermissionDto> Permissions { get; set; } = new();
}

public class GetResourcePermissionListResultDto
{
    public string ResourceName { get; set; } = "";
    public string ResourceKey { get; set; } = "";
    public List<PermissionGroupDto> Groups { get; set; } = new();
}
