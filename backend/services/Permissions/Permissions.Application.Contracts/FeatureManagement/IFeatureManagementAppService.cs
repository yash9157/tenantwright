using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;

namespace Permissions.Application.Contracts.FeatureManagement;

public interface IFeatureManagementAppService
{
    Task<GetFeatureListResultDto> GetAsync(string providerName, string providerKey, CancellationToken cancellationToken = default);

    Task UpdateAsync(string providerName, string providerKey, UpdateFeaturesDto input, CancellationToken cancellationToken = default);

    Task<BatchFeatureCheckResponseDto> BatchCheckAsync(BatchFeatureCheckRequestDto input, CancellationToken cancellationToken = default);
}
