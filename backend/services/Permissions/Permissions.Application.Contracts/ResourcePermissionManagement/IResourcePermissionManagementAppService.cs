using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;

namespace Permissions.Application.Contracts.ResourcePermissionManagement;

public interface IResourcePermissionManagementAppService
{
    Task<GetResourcePermissionListResultDto> GetAsync(string resourceName, string resourceKey, string providerName, string providerKey, CancellationToken cancellationToken = default);

    Task UpdateAsync(string resourceName, string resourceKey, string providerName, string providerKey, UpdateResourcePermissionsDto input, CancellationToken cancellationToken = default);
}
