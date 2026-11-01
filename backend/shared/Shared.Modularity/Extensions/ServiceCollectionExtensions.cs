using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Shared.Modularity.Context;
using Shared.Modularity.DependencyInjection;
using Shared.Modularity.Modules;

namespace Shared.Modularity.Extensions;

public static class ServiceCollectionExtensions
{
    public static ModuleApplication AddApplication<TStartupModule>(this IServiceCollection services, IConfiguration configuration)
        where TStartupModule : ISharedModule
    {
        if (services.Any(x =>
                x.ServiceType == typeof(IModuleApplication) ||
                x.ServiceType == typeof(ModuleApplication)))
        {
            throw new InvalidOperationException(
                "An application module has already been added to this service collection. " +
                "AddApplication<TStartupModule> can only be called once per host.");
        }

        if (ApplicationBuilderExtensions.IsBuildTimeOpenApiGeneration(Assembly.GetEntryAssembly()?.GetName().Name))
        {
            var documentGenerationApplication = new ModuleApplication(
                typeof(TStartupModule),
                services,
                configuration);
            services.AddSingleton(documentGenerationApplication);
            return documentGenerationApplication;
        }

        var normalizationResult = EnsureInstanceBackedSingleton(services, configuration);
        if (normalizationResult.ReplacedUnkeyedDescriptors > 0)
        {
            var diagnostics = GetOrAddSingletonNormalizationDiagnostics(services);
            diagnostics.Record(
                typeof(IConfiguration),
                normalizationResult.ReplacedUnkeyedDescriptors,
                normalizationResult.PreservedKeyedDescriptors);
        }

        _ = GetOrAddConventionalRegistrationOptions(services, configuration);

        services.TryAddObjectAccessor<IServiceProvider>();

        var application = new ModuleApplication(typeof(TStartupModule), services, configuration);
        application.ConfigureServices();
        services.AddSingleton(application);
        return application;
    }

    public static IConfiguration GetConfiguration(this IServiceCollection services)
    {
        return services.GetSingletonInstance<IConfiguration>();
    }

    public static T? GetSingletonInstanceOrNull<T>(this IServiceCollection services)
    {
        var serviceType = typeof(T);
        var descriptors = services
            .Where(d => d.ServiceType == serviceType && d.ServiceKey == null)
            .ToList();

        if (descriptors.Count == 0)
        {
            return default;
        }

        foreach (var descriptor in descriptors)
        {
            if (descriptor.NormalizedImplementationInstance() is T singletonInstance)
            {
                return singletonInstance;
            }
        }

        if (descriptors.Any(x => x.Lifetime == ServiceLifetime.Singleton) &&
            RequiresStrictSingletonInstanceLookup(serviceType))
        {
            throw new InvalidOperationException(
                $"Singleton service '{serviceType.AssemblyQualifiedName}' is registered without an implementation instance. " +
                "Register it as an implementation instance to use service-collection singleton lookup.");
        }

        return default;
    }

    public static T GetSingletonInstance<T>(this IServiceCollection services)
    {
        var service = services.GetSingletonInstanceOrNull<T>();
        if (service == null)
        {
            throw new InvalidOperationException("Could not find singleton service: " + typeof(T).AssemblyQualifiedName);
        }

        return service;
    }

    // Deterministic policy: normalize all unkeyed descriptors of T to one instance-backed singleton.
    // Keyed descriptors are preserved untouched.
    private static SingletonNormalizationResult EnsureInstanceBackedSingleton<T>(IServiceCollection services, T singletonInstance)
        where T : class
    {
        var serviceType = typeof(T);
        var unkeyedDescriptors = services
            .Where(d => d.ServiceType == serviceType && d.ServiceKey == null)
            .ToList();
        var preservedKeyedDescriptors = services.Count(d => d.ServiceType == serviceType && d.ServiceKey != null);

        if (unkeyedDescriptors.Count == 1 &&
            ReferenceEquals(unkeyedDescriptors[0].NormalizedImplementationInstance(), singletonInstance))
        {
            return new SingletonNormalizationResult(0, preservedKeyedDescriptors);
        }

        foreach (var descriptor in unkeyedDescriptors)
        {
            services.Remove(descriptor);
        }

        services.AddSingleton(serviceType, singletonInstance);

        return new SingletonNormalizationResult(unkeyedDescriptors.Count, preservedKeyedDescriptors);
    }

    private static SingletonNormalizationDiagnostics GetOrAddSingletonNormalizationDiagnostics(IServiceCollection services)
    {
        var existing = services
            .Where(d => d.ServiceType == typeof(SingletonNormalizationDiagnostics) && d.ServiceKey == null)
            .Select(d => d.NormalizedImplementationInstance())
            .OfType<SingletonNormalizationDiagnostics>()
            .FirstOrDefault();

        if (existing != null)
        {
            return existing;
        }

        var diagnostics = new SingletonNormalizationDiagnostics();
        services.AddSingleton(diagnostics);
        return diagnostics;
    }

    private static bool RequiresStrictSingletonInstanceLookup(Type serviceType)
    {
        if (serviceType == typeof(IConfiguration))
        {
            return false;
        }

        var assemblyName = serviceType.Assembly.GetName().Name;
        return assemblyName != null &&
               assemblyName.StartsWith("Shared.", StringComparison.Ordinal);
    }

    private static ConventionalRegistrationOptions GetOrAddConventionalRegistrationOptions(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var existing = services.GetSingletonInstanceOrNull<ConventionalRegistrationOptions>();
        if (existing != null)
        {
            return existing;
        }

        var registrationOptions = new ConventionalRegistrationOptions();
        configuration.GetSection(ConventionalRegistrationOptions.SectionName).Bind(registrationOptions);
        services.AddSingleton(registrationOptions);
        return registrationOptions;
    }

    private readonly record struct SingletonNormalizationResult(
        int ReplacedUnkeyedDescriptors,
        int PreservedKeyedDescriptors);
}
