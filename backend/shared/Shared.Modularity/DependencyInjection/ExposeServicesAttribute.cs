using System.Reflection;

namespace Shared.Modularity.DependencyInjection;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ExposeServicesAttribute : Attribute, IExposedServiceTypesProvider
{
    public Type[] ServiceTypes { get; }

    public bool IncludeDefaults { get; set; }

    public bool IncludeSelf { get; set; }

    public ExposeServicesAttribute(params Type[] serviceTypes)
    {
        ServiceTypes = serviceTypes ?? Type.EmptyTypes;
    }

    public Type[] GetExposedServiceTypes(Type targetType)
    {
        var serviceList = ServiceTypes.ToList();

        if (IncludeDefaults)
        {
            foreach (var type in GetDefaultServices(targetType))
            {
                if (!serviceList.Contains(type))
                {
                    serviceList.Add(type);
                }
            }

            if (IncludeSelf && !serviceList.Contains(targetType))
            {
                serviceList.Add(targetType);
            }
        }
        else if (IncludeSelf && !serviceList.Contains(targetType))
        {
            serviceList.Add(targetType);
        }

        return serviceList.ToArray();
    }

    private static List<Type> GetDefaultServices(Type type)
    {
        var serviceTypes = new List<Type>();

        foreach (var interfaceType in type.GetTypeInfo().GetInterfaces())
        {
            var interfaceName = interfaceType.Name;
            if (interfaceType.IsGenericType)
            {
                var tickIndex = interfaceName.IndexOf('`', StringComparison.Ordinal);
                interfaceName = tickIndex >= 0 ? interfaceName[..tickIndex] : interfaceName;
            }

            if (interfaceName.StartsWith('I'))
            {
                interfaceName = interfaceName[1..];
            }

            if (type.Name.EndsWith(interfaceName, StringComparison.OrdinalIgnoreCase))
            {
                serviceTypes.Add(interfaceType);
            }
        }

        return serviceTypes;
    }
}
