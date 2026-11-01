using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Definitions;

public class PermissionDefinitionManager : ISingletonDependency
{
    private readonly IReadOnlyList<PermissionDefinitionProvider> _providers;
    private readonly Lazy<PermissionDefinitionContext> _lazyContext;

    public PermissionDefinitionManager(IEnumerable<PermissionDefinitionProvider> providers)
    {
        _providers = providers.ToList();
        _lazyContext = new Lazy<PermissionDefinitionContext>(BuildContext, isThreadSafe: true);
    }

    public PermissionDefinitionContext GetContext()
    {
        return _lazyContext.Value;
    }

    public IReadOnlyList<PermissionGroupDefinition> GetGroups()
    {
        return _lazyContext.Value.Groups.Values.ToList();
    }

    private PermissionDefinitionContext BuildContext()
    {
        var context = new PermissionDefinitionContext();
        foreach (var provider in _providers)
        {
            provider.Define(context);
        }

        return context;
    }
}
