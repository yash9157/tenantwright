using System.Reflection;

namespace Shared.Modularity.DependencyInjection;

public sealed class ModuleAssemblyCollection
{
    private readonly HashSet<Assembly> _assemblies;

    public IReadOnlyCollection<Assembly> Assemblies => _assemblies;

    public ModuleAssemblyCollection(IEnumerable<Assembly> assemblies)
    {
        _assemblies = new HashSet<Assembly>(assemblies ?? Array.Empty<Assembly>());
    }

    public bool Contains(Assembly assembly)
    {
        return _assemblies.Contains(assembly);
    }
}
