using Shared.Modularity.DependencyInjection;
using Shared.Modularity.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionRegistrationActionExtensions
{
    public static void OnServiceRegistered(this IServiceCollection services, Action<IOnServiceRegisteredContext> registrationAction)
    {
        ArgumentNullException.ThrowIfNull(registrationAction);
        GetOrCreateRegistrationActionList(services).Add(registrationAction);
    }

    public static ServiceRegistrationActionList GetRegistrationActionList(this IServiceCollection services)
    {
        return GetOrCreateRegistrationActionList(services);
    }

    public static void DisableClassInterceptors(this IServiceCollection services)
    {
        GetOrCreateRegistrationActionList(services).IsClassInterceptorsDisabled = true;
    }

    public static void DisableClassInterceptors(this IServiceCollection services, NamedTypeSelector selector)
    {
        GetOrCreateRegistrationActionList(services).DisabledClassInterceptorsSelectors.Add(selector);
    }

    public static void OnExposing(this IServiceCollection services, Action<IOnServiceExposingContext> exposeAction)
    {
        GetOrCreateExposingActionList(services).Add(exposeAction);
    }

    public static ServiceExposingActionList GetExposingActionList(this IServiceCollection services)
    {
        return GetOrCreateExposingActionList(services);
    }

    private static ServiceRegistrationActionList GetOrCreateRegistrationActionList(IServiceCollection services)
    {
        var list = services.GetSingletonInstanceOrNull<IObjectAccessor<ServiceRegistrationActionList>>()?.Value;
        if (list != null)
        {
            return list;
        }

        list = new ServiceRegistrationActionList();
        services.AddObjectAccessor(list);
        return list;
    }

    private static ServiceExposingActionList GetOrCreateExposingActionList(IServiceCollection services)
    {
        var list = services.GetSingletonInstanceOrNull<IObjectAccessor<ServiceExposingActionList>>()?.Value;
        if (list != null)
        {
            return list;
        }

        list = new ServiceExposingActionList();
        services.AddObjectAccessor(list);
        return list;
    }
}
