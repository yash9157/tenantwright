using System.Reflection;
using Shared.Modularity.DependencyInjection;

namespace Shared.AuditLogs.Auditing;

public static class AuditingInterceptorRegistrar
{
    public static void RegisterIfNeeded(IOnServiceRegisteredContext context)
    {
        if (!ShouldIntercept(context.ImplementationType))
        {
            return;
        }

        if (!context.Interceptors.Contains(typeof(AuditingInterceptor)))
        {
            context.Interceptors.Add(typeof(AuditingInterceptor));
        }
    }

    public static bool? ShouldAuditTypeByDefaultOrNull(Type type, bool ignoreIntegrationServiceAttribute = false)
    {
        var isIntegrationService = IntegrationServiceAttribute.IsDefinedOrInherited(type);

        if (type.IsDefined(typeof(AuditedAttribute), true))
        {
            return true;
        }

        if (type.IsDefined(typeof(DisableAuditingAttribute), true))
        {
            return false;
        }

        if (isIntegrationService && !ignoreIntegrationServiceAttribute)
        {
            return false;
        }

        return null;
    }

    private static bool ShouldIntercept(Type type)
    {
        if (DynamicProxyIgnoreTypes.Contains(type))
        {
            return false;
        }

        if (ShouldAuditTypeByDefaultOrNull(type, ignoreIntegrationServiceAttribute: true) == true)
        {
            return true;
        }

        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Any(m => m.IsDefined(typeof(AuditedAttribute), true));
    }
}
