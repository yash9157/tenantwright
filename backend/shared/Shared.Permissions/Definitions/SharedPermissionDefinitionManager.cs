using Permissions.Application.Contracts.Definitions;
using Shared.Modularity.DependencyInjection;

namespace Shared.Permissions.Definitions;

public class SharedPermissionDefinitionManager : ISingletonDependency
{
    private readonly IEnumerable<PermissionDefinitionProvider> _providers;
    private readonly Lazy<PermissionDefinitionContext> _context;

    public SharedPermissionDefinitionManager(IEnumerable<PermissionDefinitionProvider> providers)
    {
        _providers = providers;
        _context = new Lazy<PermissionDefinitionContext>(Build, isThreadSafe: true);
    }

    public IReadOnlyCollection<PermissionGroupDefinition> GetGroups()
    {
        return _context.Value.Groups.Values;
    }

    private PermissionDefinitionContext Build()
    {
        var context = new PermissionDefinitionContext();
        foreach (var provider in _providers)
        {
            provider.Define(context);
        }

        return context;
    }
}
