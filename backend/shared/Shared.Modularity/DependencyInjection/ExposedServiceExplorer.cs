using System.Collections.Concurrent;

namespace Shared.Modularity.DependencyInjection;

public static class ExposedServiceExplorer
{
    private static readonly ExposeServicesAttribute DefaultExposeServicesAttribute = new()
    {
        IncludeDefaults = true,
        IncludeSelf = true
    };
    private static readonly ConcurrentDictionary<Type, Type[]> ExposedServicesCache = new();

    public static List<Type> GetExposedServices(Type type)
    {
        return ExposedServicesCache.GetOrAdd(type, static targetType =>
        {
            var providers = targetType.GetCustomAttributes(inherit: true)
                .OfType<IExposedServiceTypesProvider>()
                .ToList();

            return providers
                .DefaultIfEmpty(DefaultExposeServicesAttribute)
                .SelectMany(x => x.GetExposedServiceTypes(targetType))
                .Distinct()
                .ToArray();
        }).ToList();
    }
}
