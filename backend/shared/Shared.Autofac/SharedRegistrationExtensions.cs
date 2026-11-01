using Autofac.Builder;
using Autofac.Extras.DynamicProxy;
using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.DependencyInjection;

namespace Shared.Autofac;

public static class SharedRegistrationExtensions
{
    public static IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle>
        ConfigureSharedConventions<TLimit, TActivatorData, TRegistrationStyle>(
            this IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle> registrationBuilder,
            ServiceDescriptor serviceDescriptor,
            ModuleAssemblyCollection moduleAssemblies,
            ServiceRegistrationActionList registrationActionList,
            ServiceActivatedActionList activatedActionList)
        where TActivatorData : ReflectionActivatorData
    {
        registrationBuilder = registrationBuilder.InvokeActivatedActions(activatedActionList, serviceDescriptor);

        var serviceType = serviceDescriptor.ServiceType;

        var implementationType = registrationBuilder.ActivatorData.ImplementationType;
        if (implementationType == null)
        {
            return registrationBuilder;
        }

        registrationBuilder = registrationBuilder.EnablePropertyInjection(moduleAssemblies, implementationType);
        registrationBuilder = registrationBuilder.InvokeRegistrationActions(
            registrationActionList,
            serviceType,
            implementationType,
            serviceDescriptor.ServiceKey);

        return registrationBuilder;
    }

    private static IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle> InvokeActivatedActions<TLimit, TActivatorData, TRegistrationStyle>(
        this IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle> registrationBuilder,
        ServiceActivatedActionList activatedActionList,
        ServiceDescriptor serviceDescriptor)
        where TActivatorData : ReflectionActivatorData
    {
        var actions = activatedActionList.GetActions(serviceDescriptor);
        if (actions.Count == 0)
        {
            return registrationBuilder;
        }

        registrationBuilder.OnActivated(context =>
        {
            var activatedContext = new OnServiceActivatedContext(context.Instance!);
            foreach (var action in actions)
            {
                action.Invoke(activatedContext);
            }
        });

        return registrationBuilder;
    }

    private static IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle> InvokeRegistrationActions<TLimit, TActivatorData, TRegistrationStyle>(
        this IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle> registrationBuilder,
        ServiceRegistrationActionList registrationActionList,
        Type serviceType,
        Type implementationType,
        object? serviceKey)
        where TActivatorData : ReflectionActivatorData
    {
        var serviceRegisteredContext = new OnServiceRegisteredContext(serviceType, implementationType, serviceKey);

        foreach (var registrationAction in registrationActionList)
        {
            registrationAction.Invoke(serviceRegisteredContext);
        }

        if (serviceRegisteredContext.Interceptors.Count == 0)
        {
            return registrationBuilder;
        }

        return registrationBuilder.AddInterceptors(
            registrationActionList,
            serviceType,
            serviceRegisteredContext.Interceptors);
    }

    private static IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle> EnablePropertyInjection<TLimit, TActivatorData, TRegistrationStyle>(
        this IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle> registrationBuilder,
        ModuleAssemblyCollection moduleAssemblies,
        Type implementationType)
        where TActivatorData : ReflectionActivatorData
    {
        if (!moduleAssemblies.Contains(implementationType.Assembly))
        {
            return registrationBuilder;
        }

        if (implementationType.GetCustomAttributes(typeof(DisablePropertyInjectionAttribute), true).Length != 0)
        {
            return registrationBuilder;
        }

        return registrationBuilder.PropertiesAutowired(new SharedPropertySelector(false));
    }

    private static IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle> AddInterceptors<TLimit, TActivatorData, TRegistrationStyle>(
        this IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle> registrationBuilder,
        ServiceRegistrationActionList registrationActionList,
        Type serviceType,
        IEnumerable<Type> interceptors)
        where TActivatorData : ReflectionActivatorData
    {
        if (serviceType.IsInterface)
        {
            registrationBuilder = registrationBuilder.EnableInterfaceInterceptors();
        }
        else
        {
            var classInterceptorsDisabled =
                registrationActionList.IsClassInterceptorsDisabled
                || registrationActionList.DisabledClassInterceptorsSelectors.Any(selector => selector.Predicate(serviceType));

            if (classInterceptorsDisabled)
            {
                return registrationBuilder;
            }

            (registrationBuilder as IRegistrationBuilder<TLimit, ConcreteReflectionActivatorData, TRegistrationStyle>)
                ?.EnableClassInterceptors();
        }

        foreach (var interceptor in interceptors.Distinct())
        {
            registrationBuilder.InterceptedBy(interceptor);
        }

        return registrationBuilder;
    }
}
