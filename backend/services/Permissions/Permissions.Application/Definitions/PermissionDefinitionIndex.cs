using Permissions.Application.Contracts.Definitions;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Definitions;

public class PermissionDefinitionIndex : ISingletonDependency
{
    private readonly PermissionDefinitionManager _definitionManager;
    private readonly Lazy<Dictionary<string, PermissionDefinition>> _lazyMap;

    public PermissionDefinitionIndex(PermissionDefinitionManager definitionManager)
    {
        _definitionManager = definitionManager;
        _lazyMap = new Lazy<Dictionary<string, PermissionDefinition>>(Build, isThreadSafe: true);
    }

    public bool TryGet(string permissionName, out PermissionDefinition definition)
    {
        permissionName = permissionName.Trim();
        return _lazyMap.Value.TryGetValue(permissionName, out definition!);
    }

    public bool IsDefined(string permissionName)
    {
        return _lazyMap.Value.ContainsKey(permissionName.Trim());
    }

    public IReadOnlyCollection<string> GetAllNames()
    {
        return _lazyMap.Value.Keys;
    }

    private Dictionary<string, PermissionDefinition> Build()
    {
        var map = new Dictionary<string, PermissionDefinition>(StringComparer.Ordinal);
        foreach (var group in _definitionManager.GetGroups())
        {
            Add(map, group.Permissions);
        }

        return map;
    }

    private static void Add(Dictionary<string, PermissionDefinition> map, IEnumerable<PermissionDefinition> permissions)
    {
        foreach (var p in permissions)
        {
            map[p.Name] = p;
            if (p.Children.Count > 0)
            {
                Add(map, p.Children);
            }
        }
    }
}
