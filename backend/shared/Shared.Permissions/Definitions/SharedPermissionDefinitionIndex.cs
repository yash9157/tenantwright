using Permissions.Application.Contracts.Definitions;
using Shared.Modularity.DependencyInjection;

namespace Shared.Permissions.Definitions;

public class SharedPermissionDefinitionIndex : ISingletonDependency
{
    private readonly SharedPermissionDefinitionManager _manager;
    private readonly Lazy<Dictionary<string, PermissionDefinition>> _map;

    public SharedPermissionDefinitionIndex(SharedPermissionDefinitionManager manager)
    {
        _manager = manager;
        _map = new Lazy<Dictionary<string, PermissionDefinition>>(Build, isThreadSafe: true);
    }

    public bool TryGet(string permissionName, out PermissionDefinition definition)
    {
        permissionName = permissionName.Trim();
        return _map.Value.TryGetValue(permissionName, out definition!);
    }

    private Dictionary<string, PermissionDefinition> Build()
    {
        var map = new Dictionary<string, PermissionDefinition>(StringComparer.Ordinal);
        foreach (var group in _manager.GetGroups())
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
