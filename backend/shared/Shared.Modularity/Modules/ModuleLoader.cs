using System.Reflection;

namespace Shared.Modularity.Modules;

public static class ModuleLoader
{
    public static IReadOnlyList<Type> LoadModules(Type startupModuleType)
    {
        CheckModuleType(startupModuleType);

        var visited = new HashSet<Type>();
        var visiting = new HashSet<Type>();
        var result = new List<Type>();

        void Visit(Type moduleType)
        {
            if (!visiting.Add(moduleType))
            {
                throw new InvalidOperationException($"Circular module dependency detected at: {moduleType.FullName}");
            }

            if (!visited.Contains(moduleType))
            {
                foreach (var dependencyType in GetDependedModuleTypes(moduleType))
                {
                    CheckModuleType(dependencyType);
                    Visit(dependencyType);
                }

                visited.Add(moduleType);
                result.Add(moduleType);
            }

            visiting.Remove(moduleType);
        }

        Visit(startupModuleType);
        return result;
    }

    private static List<Type> GetDependedModuleTypes(Type moduleType)
    {
        return moduleType
            .GetCustomAttributes<DependsOnAttribute>(inherit: true)
            .SelectMany(x => x.DependedModuleTypes)
            .Distinct()
            .ToList();
    }

    private static void CheckModuleType(Type moduleType)
    {
        var typeInfo = moduleType.GetTypeInfo();
        if (!typeInfo.IsClass || typeInfo.IsAbstract || typeInfo.IsGenericTypeDefinition)
        {
            throw new ArgumentException($"Given type is not a valid module class: {moduleType.FullName}", nameof(moduleType));
        }

        if (!typeof(ISharedModule).IsAssignableFrom(moduleType))
        {
            throw new ArgumentException($"Given type does not implement {nameof(ISharedModule)}: {moduleType.FullName}", nameof(moduleType));
        }
    }
}
