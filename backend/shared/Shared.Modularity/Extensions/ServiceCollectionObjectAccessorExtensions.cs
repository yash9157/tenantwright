using Shared.Modularity.DependencyInjection;
using Shared.Modularity.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionObjectAccessorExtensions
{
    public static ObjectAccessor<T> TryAddObjectAccessor<T>(this IServiceCollection services)
    {
        if (services.Any(s => s.ServiceType == typeof(ObjectAccessor<T>)))
        {
            return services.GetSingletonInstance<ObjectAccessor<T>>();
        }

        return services.AddObjectAccessor<T>();
    }

    public static ObjectAccessor<T> AddObjectAccessor<T>(this IServiceCollection services)
    {
        return services.AddObjectAccessor(new ObjectAccessor<T>());
    }

    public static ObjectAccessor<T> AddObjectAccessor<T>(this IServiceCollection services, T obj)
    {
        return services.AddObjectAccessor(new ObjectAccessor<T>(obj));
    }

    public static ObjectAccessor<T> AddObjectAccessor<T>(this IServiceCollection services, ObjectAccessor<T> accessor)
    {
        if (services.Any(s => s.ServiceType == typeof(ObjectAccessor<T>)))
        {
            throw new InvalidOperationException(
                $"An object accessor is already registered for type: {typeof(T).AssemblyQualifiedName}");
        }

        // Insert at the beginning for faster lookup by GetSingletonInstance.
        services.Insert(0, ServiceDescriptor.Singleton(accessor));
        services.Insert(0, ServiceDescriptor.Singleton<IObjectAccessor<T>>(accessor));

        return accessor;
    }

    public static T? GetObjectOrNull<T>(this IServiceCollection services)
        where T : class
    {
        return services.GetSingletonInstanceOrNull<IObjectAccessor<T>>()?.Value;
    }

    public static T GetObject<T>(this IServiceCollection services)
        where T : class
    {
        return services.GetObjectOrNull<T>()
               ?? throw new InvalidOperationException(
                   $"Could not find object of type {typeof(T).AssemblyQualifiedName} in service collection.");
    }
}
