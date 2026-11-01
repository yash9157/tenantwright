using Permissions.Application.Contracts.PermissionManagement;

namespace Permissions.Application.Contracts.PermissionManagement;

public interface IPermissionManagementAppService
{
    Task<GetPermissionListResultDto> GetAsync(string providerName, string providerKey, CancellationToken cancellationToken = default);
    Task<GetPermissionListResultDto> GetByGroupAsync(string groupName, string providerName, string providerKey, CancellationToken cancellationToken = default);
    Task UpdateAsync(string providerName, string providerKey, UpdatePermissionsDto input, CancellationToken cancellationToken = default);
}
