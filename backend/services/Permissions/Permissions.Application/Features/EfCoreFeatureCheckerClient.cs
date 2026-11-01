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
using Permissions.Domain.FeatureManagement;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Features;

[DisableConventionalRegistration]
public class EfCoreFeatureCheckerClient : IFeatureCheckerClient, ITransientDependency
{
    private const string TenantProviderName = "T";

    private readonly IRepository<FeatureDefinitionRecord, Guid> _definitionRepo;
    private readonly IRepository<FeatureValue, Guid> _valueRepo;

    public EfCoreFeatureCheckerClient(
        IRepository<FeatureDefinitionRecord, Guid> definitionRepo,
        IRepository<FeatureValue, Guid> valueRepo)
    {
        _definitionRepo = definitionRepo;
        _valueRepo = valueRepo;
    }

    public async Task<bool> IsEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken = default)
    {
        var map = await AreEnabledAsync(tenantId, new[] { featureName }, cancellationToken);
        return map.TryGetValue(featureName.Trim(), out var enabled) && enabled;
    }

    public async Task<Dictionary<string, bool>> AreEnabledAsync(Guid? tenantId, IReadOnlyCollection<string> featureNames, CancellationToken cancellationToken = default)
    {
        var names = featureNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (names.Length == 0)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        var providerKey = tenantId?.ToString("D") ?? "host";

        var defQueryable = await _definitionRepo.GetQueryableAsync();
        var defaults = await defQueryable
            .Where(x => names.Contains(x.Name))
            .Select(x => new { x.Name, x.DefaultValue })
            .ToListAsync(cancellationToken);

        var defaultMap = defaults.ToDictionary(x => x.Name, x => x.DefaultValue ?? "false", StringComparer.Ordinal);

        var valueQueryable = await _valueRepo.GetQueryableAsync();
        var values = await valueQueryable
            .Where(x => x.ProviderName == TenantProviderName && x.ProviderKey == providerKey && names.Contains(x.Name))
            .Select(x => new { x.Name, x.Value })
            .ToListAsync(cancellationToken);

        var valueMap = values.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);

        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var value = valueMap.TryGetValue(name, out var v)
                ? v
                : (defaultMap.TryGetValue(name, out var dv) ? dv : "false");

            result[name] = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        return result;
    }
}
