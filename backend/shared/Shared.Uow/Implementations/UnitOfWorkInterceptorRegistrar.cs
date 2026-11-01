using System.Reflection;
using Shared.Modularity.DependencyInjection;

namespace Shared.Uow.Implementations;

public static class UnitOfWorkInterceptorRegistrar
{
    public static void RegisterIfNeeded(IOnServiceRegisteredContext context)
    {
        if (!ShouldIntercept(context.ImplementationType))
        {
            return;
        }

        if (!context.Interceptors.Contains(typeof(UnitOfWorkInterceptor)))
        {
            context.Interceptors.Add(typeof(UnitOfWorkInterceptor));
        }
    }

    private static bool ShouldIntercept(Type implementationType)
    {
        return !DynamicProxyIgnoreTypes.Contains(implementationType)
               && UnitOfWorkHelper.IsUnitOfWorkType(implementationType.GetTypeInfo());
    }
}
