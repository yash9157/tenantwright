using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.Caching;
using Shared.DistributedLocking;
using Shared.MultiTenancy;
using Shared.Permissions.Authorization;
using Shared.Permissions.Caching;
using Shared.Permissions.Client;
using Shared.Permissions.Definitions;
using Shared.Permissions.Features;
using Shared.Permissions.Startup;

namespace Shared.Permissions;

[DependsOn(typeof(CachingModule))]
[DependsOn(typeof(DistributedLockingModule))]
[DependsOn(typeof(MultiTenancyModule))]
public class SharedPermissionsModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddOptions<SharedPermissionClientOptions>()
            .Bind(context.Configuration.GetSection("Permissions"))
            .Validate(options => options.Enabled, "Permissions:Enabled must be true when Shared.Permissions is registered.")
            .ValidateOnStart();
        context.Services.Configure<FeatureCheckerClientOptions>(context.Configuration.GetSection("RemoteFeatureChecker"));
        context.Services.AddOptions<RoleIdentityPermissionOptions>()
            .Bind(context.Configuration.GetSection(RoleIdentityPermissionOptions.SectionName))
            .Validate(options => options.MaxActiveRoleMemberships == 32,
                "Permissions:RoleIdentity:MaxActiveRoleMemberships must be 32.")
            .ValidateOnStart();
        context.Services.AddHttpClient(nameof(RemoteFeatureAccessTokenProvider));
        context.Services.TryAddSingleton<IRemoteFeatureAccessTokenProvider, RemoteFeatureAccessTokenProvider>();

        context.Services.TryAddSingleton<PermissionProviderStampHybridCache>();
        context.Services.TryAddSingleton<PermissionDecisionHybridCache>();
        context.Services.TryAddSingleton<PermissionProviderGrantsHybridCache>();
        context.Services.TryAddSingleton<PermissionSnapshotHybridCache>();
        context.Services.TryAddSingleton<TenantFeatureValueHybridCache>();

        var definitionLoadDiagnostics = new PermissionDefinitionProviderLoadDiagnostics();
        context.Services.TryAddSingleton(definitionLoadDiagnostics);
        RegisterPermissionDefinitionProviders(context.Services, definitionLoadDiagnostics);
        RegisterFeatureDefinitionProviders(context.Services, definitionLoadDiagnostics);
        context.Services.TryAddSingleton<SharedPermissionDefinitionManager>();
        context.Services.TryAddSingleton<SharedPermissionDefinitionIndex>();
        context.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PermissionPolicyStartupValidator>());

        context.Services.Replace(ServiceDescriptor.Singleton<IPermissionChecker, ValkeyPermissionChecker>());
        context.Services.TryAddTransient<PermissionGrantChangedEventHandler>();
        context.Services.TryAddTransient<FeatureValuesChangedEventHandler>();

        context.Services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionAuthorizationHandler>());
        context.Services.Replace(ServiceDescriptor.Singleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>());
    }

    private static void RegisterPermissionDefinitionProviders(
        IServiceCollection services,
        PermissionDefinitionProviderLoadDiagnostics diagnostics)
    {
        var providerTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly => GetTypesOrEmpty(assembly, diagnostics))
            .Where(t => t is { IsAbstract: false, IsClass: true } &&
                        typeof(global::Permissions.Application.Contracts.Definitions.PermissionDefinitionProvider).IsAssignableFrom(t));

        foreach (var providerType in providerTypes.Distinct())
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton(typeof(global::Permissions.Application.Contracts.Definitions.PermissionDefinitionProvider), providerType));
        }
    }

    private static void RegisterFeatureDefinitionProviders(
        IServiceCollection services,
        PermissionDefinitionProviderLoadDiagnostics diagnostics)
    {
        var providerTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly => GetTypesOrEmpty(assembly, diagnostics))
            .Where(t => t is { IsAbstract: false, IsClass: true } &&
                        typeof(global::Permissions.Application.Contracts.Definitions.FeatureDefinitionProvider).IsAssignableFrom(t));

        foreach (var providerType in providerTypes.Distinct())
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton(typeof(global::Permissions.Application.Contracts.Definitions.FeatureDefinitionProvider), providerType));
        }
    }

    private static Type[] GetTypesOrEmpty(Assembly assembly, PermissionDefinitionProviderLoadDiagnostics diagnostics)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            diagnostics.Record(assembly, exception);
            return exception.Types.OfType<Type>().ToArray();
        }
        catch (Exception exception)
        {
            diagnostics.Record(assembly, exception);
            return Array.Empty<Type>();
        }
    }
}
