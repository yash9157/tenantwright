using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Features;

[DisableConventionalRegistration]
public class DefaultFeatureCheckerClient : IFeatureCheckerClient, ISingletonDependency
{
    public Task<bool> IsEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public Task<Dictionary<string, bool>> AreEnabledAsync(Guid? tenantId, IReadOnlyCollection<string> featureNames, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var featureName in featureNames)
        {
            if (!string.IsNullOrWhiteSpace(featureName))
            {
                result[featureName.Trim()] = true;
            }
        }

        return Task.FromResult(result);
    }
}
