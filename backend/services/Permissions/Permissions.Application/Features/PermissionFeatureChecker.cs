using Microsoft.EntityFrameworkCore;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Domain.Features;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Features;

public class PermissionFeatureChecker : ITransientDependency
{
    private readonly IRepository<PermissionFeatureBinding, Guid> _bindingRepository;
    private readonly IFeatureCheckerClient _featureChecker;

    public PermissionFeatureChecker(
        IRepository<PermissionFeatureBinding, Guid> bindingRepository,
        IFeatureCheckerClient featureChecker)
    {
        _bindingRepository = bindingRepository;
        _featureChecker = featureChecker;
    }

    public async Task<Dictionary<string, bool>> CheckAsync(
        Guid? tenantId,
        IReadOnlyCollection<string> permissionNames,
        CancellationToken cancellationToken = default)
    {
        if (permissionNames.Count == 0)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        var distinct = permissionNames.Distinct(StringComparer.Ordinal).ToArray();

        var queryable = await _bindingRepository.GetQueryableAsync();
        var bindings = await queryable
            .Where(x => x.TenantId == tenantId && distinct.Contains(x.PermissionName))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (bindings.Count == 0)
        {
            return distinct.ToDictionary(x => x, _ => true, StringComparer.Ordinal);
        }

        var result = distinct.ToDictionary(x => x, _ => true, StringComparer.Ordinal);
        var featureNames = bindings
            .Select(x => x.FeatureName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var features = await _featureChecker.AreEnabledAsync(tenantId, featureNames, cancellationToken);
        foreach (var binding in bindings)
        {
            var featureName = binding.FeatureName.Trim();
            result[binding.PermissionName] = features.TryGetValue(featureName, out var enabled) && enabled;
        }

        return result;
    }
}
