using System.Reflection;
using Autofac;
using Autofac.Builder;
using Autofac.Core;
using Autofac.Core.Activators;
using Autofac.Core.Activators.Delegate;
using Autofac.Core.Activators.Reflection;
using Autofac.Core.Resolving.Pipeline;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.DependencyInjection;
using Shared.Modularity.Extensions;

namespace Shared.Autofac;

internal static class SharedAutofacRegistration
{
    public static void Populate(ContainerBuilder builder, IServiceCollection services)
    {
        builder.RegisterType<AutofacServiceProvider>()
            .As<IServiceProvider>()
            .As<IServiceProviderIsService>()
            .As<IKeyedServiceProvider>()
            .As<IServiceProviderIsKeyedService>()
            .ExternallyOwned();

        var scopeFactoryType = typeof(AutofacServiceProvider)
            .Assembly
            .GetType("Autofac.Extensions.DependencyInjection.AutofacServiceScopeFactory");

        if (scopeFactoryType != null)
        {
            builder.RegisterType(scopeFactoryType)
                .As<IServiceScopeFactory>()
                .SingleInstance();
        }
        else
        {
            builder.RegisterType<SharedAutofacServiceScopeFactory>()
                .As<IServiceScopeFactory>()
                .SingleInstance();
        }

        builder.RegisterSource<AnyKeyRegistrationSource>();
        builder.ComponentRegistryBuilder.Registered += AddFromKeyedServiceParameterMiddleware;

        Register(builder, services);
    }

    private static void AddFromKeyedServiceParameterMiddleware(object? sender, ComponentRegisteredEventArgs eventArgs)
    {
        var needsFromKeyedServiceParameter = false;

        if (eventArgs.ComponentRegistration.Activator is ReflectionActivator reflectionActivator)
        {
            var constructors = reflectionActivator.ConstructorFinder.FindConstructors(reflectionActivator.LimitType);
            foreach (var constructor in constructors)
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    if (parameter.GetCustomAttribute<FromKeyedServicesAttribute>() != null)
                    {
                        needsFromKeyedServiceParameter = true;
                        break;
                    }
                }

                if (needsFromKeyedServiceParameter)
                {
                    break;
                }
            }
        }
        else if (eventArgs.ComponentRegistration.Activator is DelegateActivator)
        {
            needsFromKeyedServiceParameter = false;
        }
        else if (eventArgs.ComponentRegistration.Activator is InstanceActivator)
        {
            needsFromKeyedServiceParameter = false;
        }
        else
        {
            needsFromKeyedServiceParameter = true;
        }

        eventArgs.ComponentRegistration.PipelineBuilding += (_, pipelineBuilder) =>
        {
            var keyedServiceMiddlewareType = typeof(AutofacServiceProvider)
                .Assembly
                .GetType("Autofac.Extensions.DependencyInjection.KeyedServiceMiddleware");

            if (keyedServiceMiddlewareType == null)
            {
                return;
            }

            var withParam = (IResolveMiddleware)keyedServiceMiddlewareType
                .GetProperty("InstanceWithFromKeyedServicesParameter", BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null, null)!;

            var withoutParam = (IResolveMiddleware)keyedServiceMiddlewareType
                .GetProperty("InstanceWithoutFromKeyedServicesParameter", BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null, null)!;

            pipelineBuilder.Use(
                needsFromKeyedServiceParameter ? withParam : withoutParam,
                MiddlewareInsertionMode.StartOfPhase);
        };
    }

    private static void Register(ContainerBuilder builder, IServiceCollection services)
    {
        var moduleAssemblies =
            services.GetSingletonInstanceOrNull<ModuleAssemblyCollection>()
            ?? new ModuleAssemblyCollection(Array.Empty<Assembly>());
        var registrationActionList = services.GetRegistrationActionList();
        var activatedActionList = services.GetServiceActivatedActionList();

        foreach (var descriptor in services)
        {
            var implementationType = descriptor.NormalizedImplementationType();
            if (implementationType != null)
            {
                if (descriptor.ServiceType.IsGenericTypeDefinition)
                {
                    builder.RegisterGeneric(implementationType)
                        .ConfigureServiceType(descriptor)
                        .ConfigureLifecycle(descriptor.Lifetime)
                        .ConfigureSharedConventions(
                            descriptor,
                            moduleAssemblies,
                            registrationActionList,
                            activatedActionList);
                }
                else
                {
                    builder.RegisterType(implementationType)
                        .ConfigureServiceType(descriptor)
                        .ConfigureLifecycle(descriptor.Lifetime)
                        .ConfigureSharedConventions(
                            descriptor,
                            moduleAssemblies,
                            registrationActionList,
                            activatedActionList);
                }

                continue;
            }

            if (descriptor.IsKeyedService && descriptor.KeyedImplementationFactory != null)
            {
                var registration = RegistrationBuilder.ForDelegate(descriptor.ServiceType, (context, _) =>
                    {
                        var requestContext = (ResolveRequestContext)context;
                        var keyedService = (global::Autofac.Core.KeyedService)requestContext.Service;
                        var serviceProvider = context.Resolve<IServiceProvider>();
                        return descriptor.KeyedImplementationFactory(serviceProvider, keyedService.ServiceKey);
                    })
                    .ConfigureServiceType(descriptor)
                    .ConfigureLifecycle(descriptor.Lifetime)
                    .CreateRegistration();

                builder.RegisterComponent(registration);
                continue;
            }

            if (!descriptor.IsKeyedService && descriptor.ImplementationFactory != null)
            {
                var registration = RegistrationBuilder.ForDelegate(descriptor.ServiceType, (context, _) =>
                    {
                        var serviceProvider = context.Resolve<IServiceProvider>();
                        return descriptor.ImplementationFactory(serviceProvider);
                    })
                    .ConfigureServiceType(descriptor)
                    .ConfigureLifecycle(descriptor.Lifetime)
                    .CreateRegistration();

                builder.RegisterComponent(registration);
                continue;
            }

            builder.RegisterInstance(descriptor.NormalizedImplementationInstance()!)
                .ConfigureServiceType(descriptor)
                .ConfigureLifecycle(descriptor.Lifetime);
        }
    }

    private static IRegistrationBuilder<object, TActivatorData, TRegistrationStyle> ConfigureServiceType<TActivatorData, TRegistrationStyle>(
        this IRegistrationBuilder<object, TActivatorData, TRegistrationStyle> registrationBuilder,
        ServiceDescriptor descriptor)
    {
        if (descriptor.IsKeyedService)
        {
            registrationBuilder.Keyed(descriptor.ServiceKey!, descriptor.ServiceType);
        }
        else
        {
            registrationBuilder.As(descriptor.ServiceType);
        }

        return registrationBuilder;
    }

    private static IRegistrationBuilder<object, TActivatorData, TRegistrationStyle> ConfigureLifecycle<TActivatorData, TRegistrationStyle>(
        this IRegistrationBuilder<object, TActivatorData, TRegistrationStyle> registrationBuilder,
        ServiceLifetime lifetime)
    {
        switch (lifetime)
        {
            case ServiceLifetime.Singleton:
                registrationBuilder.SingleInstance();
                break;
            case ServiceLifetime.Scoped:
                registrationBuilder.InstancePerLifetimeScope();
                break;
            case ServiceLifetime.Transient:
                registrationBuilder.InstancePerDependency();
                break;
        }

        return registrationBuilder;
    }
}
