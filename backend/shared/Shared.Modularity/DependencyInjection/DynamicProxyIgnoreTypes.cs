namespace Shared.Modularity.DependencyInjection;

public static class DynamicProxyIgnoreTypes
{
    private static readonly HashSet<Type> IgnoredTypes = new();

    public static void Add<T>()
    {
        Add(typeof(T));
    }

    public static void Add(Type type)
    {
        lock (IgnoredTypes)
        {
            IgnoredTypes.Add(type);
        }
    }

    public static void Add(params Type[] types)
    {
        lock (IgnoredTypes)
        {
            foreach (var type in types)
            {
                IgnoredTypes.Add(type);
            }
        }
    }

    public static bool Contains(Type type, bool includeDerivedTypes = true)
    {
        lock (IgnoredTypes)
        {
            if (includeDerivedTypes)
            {
                return IgnoredTypes.Any(t => t.IsAssignableFrom(type));
            }

            return IgnoredTypes.Contains(type);
        }
    }

    public static void Remove(Type type)
    {
        lock (IgnoredTypes)
        {
            IgnoredTypes.Remove(type);
        }
    }

    public static void Clear()
    {
        lock (IgnoredTypes)
        {
            IgnoredTypes.Clear();
        }
    }
}
