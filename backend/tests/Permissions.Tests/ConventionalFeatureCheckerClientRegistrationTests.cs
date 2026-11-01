using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Permissions.Application;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Definitions;
using Permissions.Application.Features;
using Permissions.Application.ImportExport;
using Shared.Modularity.Context;
using Shared.Modularity.DependencyInjection;
using Shared.Permissions;
using Shared.Permissions.Definitions.Auth;
using Shared.Permissions.Definitions.Identity;
using Shared.Permissions.Definitions.Tenant;
using Shared.Permissions.Definitions.Notifications;
using Shared.Permissions.Features;
using Xunit;

namespace Permissions.Tests;

public class ConventionalFeatureCheckerClientRegistrationTests
{
    [Fact]
    public void Strict_conventional_scan_across_Permissions_and_SharedPermissions_should_not_throw_for_IFeatureCheckerClient()
    {
        var services = new ServiceCollection();
        var options = new ConventionalRegistrationOptions
        {
            ExposureMode = ConventionalServiceExposureMode.Default,
            FailOnDuplicateRegistrations = true
        };

        var exception = Record.Exception(() =>
        {
            ConventionalRegistrar.RegisterAssembly(
                services,
                typeof(PermissionsApplicationModule).Assembly,
                options);

            ConventionalRegistrar.RegisterAssembly(
                services,
                typeof(SharedPermissionsModule).Assembly,
                options);
        });

        Assert.Null(exception);
        Assert.Empty(services.Where(x =>
            x.ServiceType == typeof(IFeatureCheckerClient) &&
            x.ServiceKey == null));
    }

    [Fact]
    public void PermissionsApplicationModule_should_register_explicit_feature_checker_factory_and_concrete_clients()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RemoteFeatureChecker:Enabled"] = "false"
            })
            .Build();
        var context = new ServiceConfigurationContext(services, configuration);

        new PermissionsApplicationModule().ConfigureServices(context);

        var featureCheckerDescriptor = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(IFeatureCheckerClient) &&
            x.ServiceKey == null));
        Assert.Equal(ServiceLifetime.Transient, featureCheckerDescriptor.Lifetime);
        Assert.NotNull(featureCheckerDescriptor.ImplementationFactory);

        var defaultDescriptor = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(DefaultFeatureCheckerClient) &&
            x.ServiceKey == null));
        Assert.Equal(ServiceLifetime.Singleton, defaultDescriptor.Lifetime);

        var efCoreDescriptor = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(EfCoreFeatureCheckerClient) &&
            x.ServiceKey == null));
        Assert.Equal(ServiceLifetime.Transient, efCoreDescriptor.Lifetime);

        var httpDescriptor = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(HttpFeatureCheckerClient) &&
            x.ServiceKey == null));
        Assert.Equal(ServiceLifetime.Transient, httpDescriptor.Lifetime);
    }

    [Fact]
    public void PermissionsApplicationModule_should_register_shared_permission_definition_providers()
    {
        var services = new ServiceCollection();
        var context = new ServiceConfigurationContext(services, new ConfigurationBuilder().Build());

        new PermissionsApplicationModule().ConfigureServices(context);

        Assert.Contains(services, x =>
            x.ServiceType == typeof(PermissionDefinitionProvider) &&
            x.ImplementationType == typeof(IdentityPermissionDefinitionProvider));
        Assert.Contains(services, x =>
            x.ServiceType == typeof(PermissionDefinitionProvider) &&
            x.ImplementationType == typeof(TenantPermissionDefinitionProvider));
        Assert.Contains(services, x =>
            x.ServiceType == typeof(PermissionDefinitionProvider) &&
            x.ImplementationType == typeof(AuthPermissionDefinitionProvider));
        Assert.Contains(services, x =>
            x.ServiceType == typeof(PermissionDefinitionProvider) &&
            x.ImplementationType == typeof(NotificationsPermissionDefinitionProvider));
    }

    [Fact]
    public void Strict_conventional_scan_should_expose_import_transaction_through_its_interface()
    {
        var services = new ServiceCollection();
        var options = new ConventionalRegistrationOptions
        {
            ExposureMode = ConventionalServiceExposureMode.Default,
            FailOnDuplicateRegistrations = true,
        };

        ConventionalRegistrar.RegisterAssembly(services, typeof(PermissionsApplicationModule).Assembly, options);

        var descriptor = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(IPermissionImportApplyTransaction)));
        Assert.Equal(typeof(PermissionImportApplyTransaction), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(PermissionImportApplyTransaction));
    }

    [Fact]
    public void PermissionsApplicationModule_service_provider_should_resolve_shared_dashboard_definitions()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RemoteFeatureChecker:Enabled"] = "false"
            })
            .Build();
        var context = new ServiceConfigurationContext(services, configuration);
        var registrationOptions = new ConventionalRegistrationOptions
        {
            ExposureMode = ConventionalServiceExposureMode.Default,
            FailOnDuplicateRegistrations = true
        };

        ConventionalRegistrar.RegisterAssembly(services, typeof(PermissionsApplicationModule).Assembly, registrationOptions);
        ConventionalRegistrar.RegisterAssembly(services, typeof(SharedPermissionsModule).Assembly, registrationOptions);
        new PermissionsApplicationModule().ConfigureServices(context);
        new SharedPermissionsModule().ConfigureServices(context);

        using var provider = services.BuildServiceProvider();
        var index = provider.GetRequiredService<PermissionDefinitionIndex>();

        Assert.True(index.IsDefined(IdentityPermissions.Dashboard.Admin));
        Assert.True(index.IsDefined(TenantPermissions.Dashboard.Admin));
    }
}
