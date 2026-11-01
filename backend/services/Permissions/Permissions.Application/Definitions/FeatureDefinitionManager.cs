using Permissions.Application.Contracts.Definitions;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Definitions;

public class FeatureDefinitionManager : ISingletonDependency
{
    private readonly IEnumerable<FeatureDefinitionProvider> _providers;
    private readonly Lazy<FeatureDefinitionContext> _context;

    public FeatureDefinitionManager(IEnumerable<FeatureDefinitionProvider> providers)
    {
        _providers = providers;
        _context = new Lazy<FeatureDefinitionContext>(Build, isThreadSafe: true);
    }

    public IReadOnlyCollection<FeatureGroupDefinition> GetGroups()
    {
        return _context.Value.Groups.Values;
    }

    private FeatureDefinitionContext Build()
    {
        var context = new FeatureDefinitionContext();
        foreach (var provider in _providers)
        {
            provider.Define(context);
        }

        return context;
    }
}
