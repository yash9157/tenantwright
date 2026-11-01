using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity;
using Shared.Modularity.DependencyInjection;
using Shared.Modularity.Extensions;
using Shared.Modularity.Modules;
using Xunit;

namespace Shared.Modularity.Tests;

public class StrictModularityHardeningTests
{
    [Fact]
    public void Keyed_replace_should_replace_only_matching_service_key()
    {
        var services = new ServiceCollection();
        services.AddKeyedTransient<IKeyedReplaceService, ExistingKeyedReplaceService>("alpha");
        services.AddKeyedTransient<IKeyedReplaceService, ExistingKeyedReplaceService>("beta");
        AddKeyedExposure(services, typeof(IKeyedReplaceService), "alpha");

        ConventionalRegistrar.RegisterAssembly(
            services,
            typeof(StrictModularityHardeningTests).Assembly,
            new ConventionalRegistrationOptions
            {
                ExposureMode = ConventionalServiceExposureMode.Default
            });

        var alphaRegistration = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(IKeyedReplaceService) &&
            Equals(x.ServiceKey, "alpha")));
        Assert.Equal(typeof(KeyedReplaceCandidate), alphaRegistration.NormalizedImplementationType());

        var betaRegistration = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(IKeyedReplaceService) &&
            Equals(x.ServiceKey, "beta")));
        Assert.Equal(typeof(ExistingKeyedReplaceService), betaRegistration.NormalizedImplementationType());
    }

    [Fact]
    public void Keyed_try_register_should_skip_when_matching_key_exists()
    {
        var services = new ServiceCollection();
        services.AddKeyedTransient<IKeyedTryService, ExistingKeyedTryService>("alpha");
        AddKeyedExposure(services, typeof(IKeyedTryService), "alpha");

        ConventionalRegistrar.RegisterAssembly(
            services,
            typeof(StrictModularityHardeningTests).Assembly,
            new ConventionalRegistrationOptions
            {
                ExposureMode = ConventionalServiceExposureMode.Default
            });

        var registration = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(IKeyedTryService) &&
            Equals(x.ServiceKey, "alpha")));

        Assert.Equal(typeof(ExistingKeyedTryService), registration.NormalizedImplementationType());
    }

    [Fact]
    public void Duplicate_descriptor_should_throw_when_duplicate_guard_enabled()
    {
        var services = new ServiceCollection();
        var options = new ConventionalRegistrationOptions
        {
            ExposureMode = ConventionalServiceExposureMode.Default,
            FailOnDuplicateRegistrations = true
        };

        ConventionalRegistrar.RegisterAssembly(
            services,
            typeof(StrictModularityHardeningTests).Assembly,
            options);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConventionalRegistrar.RegisterAssembly(
                services,
                typeof(StrictModularityHardeningTests).Assembly,
                options));

        Assert.Contains("Duplicate conventional registration detected", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddApplication_should_dedupe_module_assemblies_for_conventional_scan()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(ConventionalServiceExposureMode.Default);

        services.AddApplication<TestStartupModule>(configuration);

        var diagnostics = services.GetSingletonInstance<ConventionalRegistrationDiagnostics>();
        Assert.Equal(1, diagnostics.ScannedAssemblies);
    }

    [Fact]
    public void AddApplication_should_fail_when_legacy_exposure_mode_is_selected_in_strict_mode()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(ConventionalServiceExposureMode.DefaultWithLegacyAliases);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddApplication<TestStartupModule>(configuration));

        Assert.Contains("strict mode requires ExposureMode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddApplication_should_replace_factory_configuration_registration_with_instance()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(ConventionalServiceExposureMode.Default);

        services.AddSingleton<IConfiguration>(_ => configuration);

        services.AddApplication<TestStartupModule>(configuration);

        var configurationDescriptors = services
            .Where(x => x.ServiceType == typeof(IConfiguration) && x.ServiceKey == null)
            .ToList();

        var descriptor = Assert.Single(configurationDescriptors);
        Assert.Same(configuration, descriptor.NormalizedImplementationInstance());
        Assert.Same(configuration, services.GetSingletonInstance<IConfiguration>());

        var normalizationDiagnostics = services.GetSingletonInstanceOrNull<SingletonNormalizationDiagnostics>();
        Assert.NotNull(normalizationDiagnostics);
        var record = Assert.Single(normalizationDiagnostics!.Records);
        Assert.Equal(typeof(IConfiguration).FullName, record.ServiceType);
        Assert.Equal(1, record.ReplacedUnkeyedDescriptors);
        Assert.Equal(0, record.PreservedKeyedDescriptors);
    }

    [Fact]
    public void AddApplication_should_collapse_multiple_configuration_registrations_to_single_instance()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(ConventionalServiceExposureMode.Default);

        services.AddSingleton<IConfiguration>(_ => configuration);
        services.AddSingleton<IConfiguration>(_ => configuration);

        services.AddApplication<TestStartupModule>(configuration);

        var configurationDescriptors = services
            .Where(x => x.ServiceType == typeof(IConfiguration) && x.ServiceKey == null)
            .ToList();

        var descriptor = Assert.Single(configurationDescriptors);
        Assert.Same(configuration, descriptor.NormalizedImplementationInstance());

        var normalizationDiagnostics = services.GetSingletonInstanceOrNull<SingletonNormalizationDiagnostics>();
        Assert.NotNull(normalizationDiagnostics);
        var record = Assert.Single(normalizationDiagnostics!.Records);
        Assert.Equal(typeof(IConfiguration).FullName, record.ServiceType);
        Assert.Equal(2, record.ReplacedUnkeyedDescriptors);
        Assert.Equal(0, record.PreservedKeyedDescriptors);
    }

    [Fact]
    public void AddApplication_should_normalize_configuration_when_instance_and_factory_both_exist()
    {
        var services = new ServiceCollection();
        var staleConfiguration = new ConfigurationBuilder().Build();
        var configuration = CreateConfiguration(ConventionalServiceExposureMode.Default);

        services.AddSingleton<IConfiguration>(staleConfiguration);
        services.AddSingleton<IConfiguration>(_ => staleConfiguration);

        services.AddApplication<TestStartupModule>(configuration);

        var configurationDescriptors = services
            .Where(x => x.ServiceType == typeof(IConfiguration) && x.ServiceKey == null)
            .ToList();

        var descriptor = Assert.Single(configurationDescriptors);
        Assert.Same(configuration, descriptor.NormalizedImplementationInstance());

        var normalizationDiagnostics = services.GetSingletonInstanceOrNull<SingletonNormalizationDiagnostics>();
        Assert.NotNull(normalizationDiagnostics);
        var record = Assert.Single(normalizationDiagnostics!.Records);
        Assert.Equal(typeof(IConfiguration).FullName, record.ServiceType);
        Assert.Equal(2, record.ReplacedUnkeyedDescriptors);
        Assert.Equal(0, record.PreservedKeyedDescriptors);
    }

    [Fact]
    public void AddApplication_should_keep_keyed_configuration_descriptors_untouched()
    {
        var services = new ServiceCollection();
        var keyedConfiguration = new ConfigurationBuilder().Build();
        var configuration = CreateConfiguration(ConventionalServiceExposureMode.Default);

        services.AddKeyedSingleton<IConfiguration>("tenant", keyedConfiguration);
        services.AddSingleton<IConfiguration>(_ => configuration);

        services.AddApplication<TestStartupModule>(configuration);

        var keyedDescriptor = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(IConfiguration) &&
            Equals(x.ServiceKey, "tenant")));

        Assert.Same(keyedConfiguration, keyedDescriptor.NormalizedImplementationInstance());

        var unkeyedDescriptor = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(IConfiguration) &&
            x.ServiceKey == null));

        Assert.Same(configuration, unkeyedDescriptor.NormalizedImplementationInstance());

        var normalizationDiagnostics = services.GetSingletonInstanceOrNull<SingletonNormalizationDiagnostics>();
        Assert.NotNull(normalizationDiagnostics);
        var record = Assert.Single(normalizationDiagnostics!.Records);
        Assert.Equal(typeof(IConfiguration).FullName, record.ServiceType);
        Assert.Equal(1, record.ReplacedUnkeyedDescriptors);
        Assert.Equal(1, record.PreservedKeyedDescriptors);
    }

    [Fact]
    public void GetSingletonInstanceOrNull_should_fail_for_shared_infra_singleton_registered_without_instance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ConventionalRegistrationOptions>(_ => new ConventionalRegistrationOptions());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.GetSingletonInstanceOrNull<ConventionalRegistrationOptions>());

        Assert.Contains("registered without an implementation instance", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSingletonInstanceOrNull_should_not_throw_for_framework_singleton_registered_without_instance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());

        var configuration = services.GetSingletonInstanceOrNull<IConfiguration>();

        Assert.Null(configuration);
    }

    [Fact]
    public void AddApplication_should_fail_on_second_call_for_same_service_collection()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(ConventionalServiceExposureMode.Default);

        services.AddApplication<TestStartupModule>(configuration);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddApplication<TestStartupModule>(configuration));

        Assert.Contains("already been added", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration CreateConfiguration(ConventionalServiceExposureMode exposureMode)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Shared:DependencyInjection:ExposureMode"] = exposureMode.ToString(),
                ["Shared:DependencyInjection:FailOnLegacyExposureMode"] = "true",
                ["Shared:DependencyInjection:FailOnDuplicateRegistrations"] = "true"
            })
            .Build();
    }

    private static void AddKeyedExposure(IServiceCollection services, Type serviceType, string serviceKey)
    {
        services.OnExposing(context =>
        {
            if (!serviceType.IsAssignableFrom(context.ImplementationType))
            {
                return;
            }

            for (var i = 0; i < context.ExposedTypes.Count; i++)
            {
                var exposedType = context.ExposedTypes[i];
                if (exposedType.ServiceType == serviceType)
                {
                    context.ExposedTypes[i] = new ServiceIdentifier(serviceKey, serviceType);
                }
            }
        });
    }

    public interface IKeyedReplaceService
    {
    }

    public sealed class ExistingKeyedReplaceService : IKeyedReplaceService
    {
    }

    [Dependency(ServiceLifetime.Transient, ReplaceServices = true)]
    [ExposeServices(typeof(IKeyedReplaceService))]
    public sealed class KeyedReplaceCandidate : IKeyedReplaceService
    {
    }

    public interface IKeyedTryService
    {
    }

    public sealed class ExistingKeyedTryService : IKeyedTryService
    {
    }

    [Dependency(ServiceLifetime.Transient, TryRegister = true)]
    [ExposeServices(typeof(IKeyedTryService))]
    public sealed class KeyedTryCandidate : IKeyedTryService
    {
    }

    [DependsOn(typeof(TestDependencyModule))]
    public sealed class TestStartupModule : BaseModule
    {
    }

    public sealed class TestDependencyModule : BaseModule
    {
    }
}
