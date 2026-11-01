using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.Extensions;

namespace Shared.Modularity.DependencyInjection;

public static class ConventionalRegistrar
{
    private static readonly ConcurrentDictionary<Assembly, Type[]> AssemblyTypesCache = new();

    public static void RegisterAssembly(
        IServiceCollection services,
        Assembly assembly,
        ConventionalRegistrationOptions? options = null)
    {
        var registrationOptions =
            options
            ?? services.GetSingletonInstanceOrNull<ConventionalRegistrationOptions>()
            ?? new ConventionalRegistrationOptions();

        var report = GetOrCreateReport(services);
        var diagnostics = GetOrCreateDiagnostics(services);
        var types = GetAssemblyTypes(assembly);

        diagnostics.AddScannedAssembly();
        diagnostics.AddScannedTypes(types.Length);

        foreach (var type in types)
        {
            AddType(services, type, registrationOptions, report, diagnostics);
        }
    }

    private static void AddType(
        IServiceCollection services,
        Type type,
        ConventionalRegistrationOptions options,
        ConventionalRegistrationReport report,
        ConventionalRegistrationDiagnostics diagnostics)
    {
        if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
        {
            return;
        }

        if (type.IsDefined(typeof(DisableConventionalRegistrationAttribute), inherit: true))
        {
            return;
        }

        var dependencyAttribute = type.GetCustomAttribute<DependencyAttribute>(inherit: true);
        var lifetime = GetLifetimeOrNull(type, dependencyAttribute);
        if (lifetime == null)
        {
            return;
        }

        var exposedServiceTypes = GetExposedServiceTypes(type, options, report);
        TriggerServiceExposing(services, type, exposedServiceTypes);

        var replaceServices = dependencyAttribute?.ReplaceServices == true;
        var tryRegister = dependencyAttribute?.TryRegister == true;

        foreach (var exposedServiceType in exposedServiceTypes)
        {
            var allExposingServiceTypes =
                exposedServiceType.ServiceKey == null
                    ? exposedServiceTypes.Where(x => x.ServiceKey == null).ToList()
                    : exposedServiceTypes.Where(x => Equals(x.ServiceKey, exposedServiceType.ServiceKey)).ToList();

            var candidate = CreateServiceDescriptor(
                implementationType: type,
                serviceKey: exposedServiceType.ServiceKey,
                exposingServiceType: exposedServiceType.ServiceType,
                allExposingServiceTypes: allExposingServiceTypes,
                lifetime: lifetime.Value);

            ApplyServiceDescriptor(
                services,
                candidate,
                options,
                type,
                replaceServices,
                tryRegister,
                diagnostics);
        }
    }

    private static void ApplyServiceDescriptor(
        IServiceCollection services,
        DescriptorCandidate candidate,
        ConventionalRegistrationOptions options,
        Type sourceType,
        bool replaceServices,
        bool tryRegister,
        ConventionalRegistrationDiagnostics diagnostics)
    {
        var descriptor = candidate.Descriptor;
        var serviceIdentifier = CreateServiceIdentifier(descriptor);
        var sourceTypeName = sourceType.FullName ?? sourceType.Name;
        var sourceAssembly = sourceType.Assembly;

        if (replaceServices)
        {
            ReplaceByServiceAndKey(services, serviceIdentifier, diagnostics);
            services.Add(descriptor);
            diagnostics.AddRegisteredDescriptor();
            diagnostics.RecordDescriptor(serviceIdentifier, candidate.ImplementationIdentity, descriptor.Lifetime, sourceAssembly, sourceTypeName);
            return;
        }

        if (tryRegister)
        {
            if (ContainsServiceAndKey(services, serviceIdentifier))
            {
                return;
            }

            services.Add(descriptor);
            diagnostics.AddRegisteredDescriptor();
            diagnostics.RecordDescriptor(serviceIdentifier, candidate.ImplementationIdentity, descriptor.Lifetime, sourceAssembly, sourceTypeName);
            return;
        }

        if (options.FailOnDuplicateRegistrations)
        {
            if (diagnostics.ContainsExactDescriptor(serviceIdentifier, candidate.ImplementationIdentity, descriptor.Lifetime))
            {
                diagnostics.AddDuplicateCollision();
                throw new InvalidOperationException(
                    $"Duplicate conventional registration detected. " +
                    $"ServiceType='{serviceIdentifier.ServiceType.FullName}', ServiceKey='{FormatServiceKey(serviceIdentifier.ServiceKey)}', " +
                    $"ImplementationType='{candidate.ImplementationIdentity.FullName}', Lifetime='{descriptor.Lifetime}', " +
                    $"SourceType='{sourceTypeName}', SourceAssembly='{sourceAssembly.GetName().Name}'. " +
                    "Use [Dependency(ReplaceServices = true)] or [Dependency(TryRegister = true)] to make intent explicit.");
            }

            if (diagnostics.TryGetServiceRegistration(serviceIdentifier, out var existing))
            {
                diagnostics.AddAmbiguousCollision();
                throw new InvalidOperationException(
                    $"Ambiguous conventional registration detected for the same service contract. " +
                    $"ServiceType='{serviceIdentifier.ServiceType.FullName}', ServiceKey='{FormatServiceKey(serviceIdentifier.ServiceKey)}'. " +
                    $"ExistingImplementation='{existing.ImplementationIdentity.FullName}', ExistingLifetime='{existing.Lifetime}', ExistingSourceType='{existing.SourceTypeName}', ExistingSourceAssembly='{existing.SourceAssembly.GetName().Name}'. " +
                    $"NewImplementation='{candidate.ImplementationIdentity.FullName}', NewLifetime='{descriptor.Lifetime}', NewSourceType='{sourceTypeName}', NewSourceAssembly='{sourceAssembly.GetName().Name}'. " +
                    "Use [Dependency(ReplaceServices = true)] or [Dependency(TryRegister = true)] to avoid ambiguous DI contracts.");
            }
        }

        services.Add(descriptor);
        diagnostics.AddRegisteredDescriptor();
        diagnostics.RecordDescriptor(serviceIdentifier, candidate.ImplementationIdentity, descriptor.Lifetime, sourceAssembly, sourceTypeName);
    }

    private static ServiceLifetime? GetLifetimeOrNull(Type type, DependencyAttribute? attribute)
    {
        if (attribute != null)
        {
            return attribute.Lifetime;
        }

        if (typeof(ITransientDependency).IsAssignableFrom(type))
        {
            return ServiceLifetime.Transient;
        }

        if (typeof(ISingletonDependency).IsAssignableFrom(type))
        {
            return ServiceLifetime.Singleton;
        }

        if (typeof(IScopedDependency).IsAssignableFrom(type))
        {
            return ServiceLifetime.Scoped;
        }

        return null;
    }

    private static List<ServiceIdentifier> GetExposedServiceTypes(
        Type type,
        ConventionalRegistrationOptions options,
        ConventionalRegistrationReport report)
    {
        if (options.ExposureMode == ConventionalServiceExposureMode.LegacyAllInterfaces)
        {
            return GetLegacyExposedServiceTypes(type).ConvertAll(x => new ServiceIdentifier(x));
        }

        var exposed = ExposedServiceExplorer.GetExposedServices(type)
            .ConvertAll(x => new ServiceIdentifier(x));

        if (options.ExposureMode != ConventionalServiceExposureMode.DefaultWithLegacyAliases)
        {
            return exposed;
        }

        var currentSet = new HashSet<ServiceIdentifier>(exposed);
        foreach (var legacyType in GetLegacyExposedServiceTypes(type))
        {
            var legacyIdentifier = new ServiceIdentifier(legacyType);
            if (currentSet.Add(legacyIdentifier))
            {
                exposed.Add(legacyIdentifier);
                report.AddLegacyAlias(type, legacyType);
            }
        }

        return exposed;
    }

    private static List<Type> GetLegacyExposedServiceTypes(Type type)
    {
        var interfaces = type.GetInterfaces()
            .Where(i =>
                i != typeof(ISingletonDependency) &&
                i != typeof(IScopedDependency) &&
                i != typeof(ITransientDependency))
            .ToList();

        interfaces.Add(type);
        return interfaces.Distinct().ToList();
    }

    private static void TriggerServiceExposing(
        IServiceCollection services,
        Type implementationType,
        List<ServiceIdentifier> serviceTypes)
    {
        var exposingActions = services.GetExposingActionList();
        if (exposingActions.Count == 0)
        {
            return;
        }

        var context = new OnServiceExposingContext(implementationType, serviceTypes);
        foreach (var action in exposingActions)
        {
            action.Invoke(context);
        }
    }

    private static DescriptorCandidate CreateServiceDescriptor(
        Type implementationType,
        object? serviceKey,
        Type exposingServiceType,
        List<ServiceIdentifier> allExposingServiceTypes,
        ServiceLifetime lifetime)
    {
        if (lifetime == ServiceLifetime.Singleton || lifetime == ServiceLifetime.Scoped)
        {
            var redirectedType = GetRedirectedTypeOrNull(
                implementationType,
                exposingServiceType,
                allExposingServiceTypes);

            if (redirectedType != null)
            {
                var redirectedDescriptor = serviceKey == null
                    ? ServiceDescriptor.Describe(
                        exposingServiceType,
                        provider => provider.GetRequiredService(redirectedType),
                        lifetime)
                    : ServiceDescriptor.DescribeKeyed(
                        exposingServiceType,
                        serviceKey,
                        (provider, key) => provider.GetRequiredKeyedService(redirectedType, key),
                        lifetime);

                return new DescriptorCandidate(redirectedDescriptor, redirectedType);
            }
        }

        var descriptor = serviceKey == null
            ? ServiceDescriptor.Describe(exposingServiceType, implementationType, lifetime)
            : ServiceDescriptor.DescribeKeyed(exposingServiceType, serviceKey, implementationType, lifetime);

        return new DescriptorCandidate(descriptor, implementationType);
    }

    private static Type? GetRedirectedTypeOrNull(
        Type implementationType,
        Type exposingServiceType,
        List<ServiceIdentifier> allExposingServiceTypes)
    {
        if (allExposingServiceTypes.Count < 2)
        {
            return null;
        }

        if (implementationType == exposingServiceType)
        {
            return null;
        }

        if (allExposingServiceTypes.Any(x => x.ServiceType == implementationType))
        {
            return implementationType;
        }

        return allExposingServiceTypes
            .Select(x => x.ServiceType)
            .FirstOrDefault(type => type != exposingServiceType && exposingServiceType.IsAssignableFrom(type));
    }

    private static ConventionalRegistrationReport GetOrCreateReport(IServiceCollection services)
    {
        var report = services.GetSingletonInstanceOrNull<ConventionalRegistrationReport>();
        if (report != null)
        {
            return report;
        }

        report = new ConventionalRegistrationReport();
        services.AddSingleton(report);
        return report;
    }

    private static ConventionalRegistrationDiagnostics GetOrCreateDiagnostics(IServiceCollection services)
    {
        var diagnostics = services.GetSingletonInstanceOrNull<ConventionalRegistrationDiagnostics>();
        if (diagnostics != null)
        {
            return diagnostics;
        }

        diagnostics = new ConventionalRegistrationDiagnostics();
        foreach (var descriptor in services)
        {
            diagnostics.SeedExistingDescriptor(descriptor);
        }

        services.AddSingleton(diagnostics);
        return diagnostics;
    }

    private static ServiceIdentifier CreateServiceIdentifier(ServiceDescriptor descriptor)
    {
        return descriptor.ServiceKey == null
            ? new ServiceIdentifier(descriptor.ServiceType)
            : new ServiceIdentifier(descriptor.ServiceKey, descriptor.ServiceType);
    }

    private static void ReplaceByServiceAndKey(
        IServiceCollection services,
        ServiceIdentifier serviceIdentifier,
        ConventionalRegistrationDiagnostics diagnostics)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (MatchesServiceIdentifier(services[i], serviceIdentifier))
            {
                services.RemoveAt(i);
            }
        }

        diagnostics.RemoveService(serviceIdentifier);
    }

    private static bool ContainsServiceAndKey(IServiceCollection services, ServiceIdentifier serviceIdentifier)
    {
        return services.Any(descriptor => MatchesServiceIdentifier(descriptor, serviceIdentifier));
    }

    private static bool MatchesServiceIdentifier(ServiceDescriptor descriptor, ServiceIdentifier serviceIdentifier)
    {
        if (descriptor.ServiceType != serviceIdentifier.ServiceType)
        {
            return false;
        }

        return Equals(descriptor.ServiceKey, serviceIdentifier.ServiceKey);
    }

    private static string FormatServiceKey(object? serviceKey)
    {
        return serviceKey?.ToString() ?? "<null>";
    }

    private static Type[] GetAssemblyTypes(Assembly assembly)
    {
        return AssemblyTypesCache.GetOrAdd(assembly, static targetAssembly =>
        {
            try
            {
                return targetAssembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null).Cast<Type>().ToArray();
            }
        });
    }

    private readonly record struct DescriptorCandidate(ServiceDescriptor Descriptor, Type ImplementationIdentity);
}
