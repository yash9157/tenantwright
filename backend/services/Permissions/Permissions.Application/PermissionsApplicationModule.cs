using Permissions.Application.Contracts;
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
using Permissions.Application.Caching;
using Permissions.Application.Features;
using Permissions.Application.ImportExport;
using Permissions.Application.PermissionCatalog;
using Permissions.Application.PermissionManagement;
using Permissions.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shared.BackgroundTasks;
using Shared.BackgroundTasks.DependencyInjection;
using Shared.Caching;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using System.Reflection;
using Microsoft.Extensions.Options;
using Shared.Permissions;
using Shared.Permissions.Features;

namespace Permissions.Application;

[DependsOn(typeof(PermissionsDomainModule))]
[DependsOn(typeof(PermissionsApplicationContractsModule))]
[DependsOn(typeof(SharedBackgroundTasksModule))]
[DependsOn(typeof(CachingModule))]
public class PermissionsApplicationModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddTransient<PermissionCatalogManager>();
        RegisterPermissionDefinitionProviders(context.Services);
        RegisterFeatureDefinitionProviders(context.Services);
        ConfigureFeatureChecker(context);
        ConfigureIdentityPermissionTargetValidation(context);
        ConfigurePermissionCacheRefresh(context);
    }

    private static void RegisterPermissionDefinitionProviders(IServiceCollection services)
    {
        var allTypes = GetCatalogAssemblies()
            .SelectMany(GetTypesOrEmpty)
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(PermissionDefinitionProvider).IsAssignableFrom(t))
            .Distinct()
            .ToList();

        // TryAddEnumerable de-duplicates by implementation type so providers also registered by
        // SharedPermissionsModule are executed once, not twice (a double run doubles every static
        // permission in the catalog).
        foreach (var providerType in allTypes)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(PermissionDefinitionProvider), providerType));
        }
    }

    private static void RegisterFeatureDefinitionProviders(IServiceCollection services)
    {
        var providerTypes = GetCatalogAssemblies()
            .SelectMany(GetTypesOrEmpty)
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(FeatureDefinitionProvider).IsAssignableFrom(t));

        foreach (var providerType in providerTypes.Distinct())
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton(typeof(Permissions.Application.Contracts.Definitions.FeatureDefinitionProvider), providerType));
        }
    }

    private static Assembly[] GetCatalogAssemblies() =>
    [
        typeof(PermissionDefinitionProvider).Assembly,
        typeof(PermissionsApplicationModule).Assembly,
        typeof(Shared.Permissions.Definitions.AuditLogs.AuditLogsPermissionDefinitionProvider).Assembly,
        typeof(Shared.Permissions.Definitions.Auth.AuthPermissionDefinitionProvider).Assembly,
        typeof(Shared.Permissions.Definitions.Identity.IdentityPermissionDefinitionProvider).Assembly,
        typeof(Shared.Permissions.Definitions.Notifications.NotificationsPermissionDefinitionProvider).Assembly,
        typeof(Shared.Permissions.Definitions.Permissions.PermissionsPermissionDefinitionProvider).Assembly,
        typeof(Shared.Permissions.Definitions.Tenant.TenantPermissionDefinitionProvider).Assembly
    ];

    private static Type[] GetTypesOrEmpty(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null).Cast<Type>().ToArray();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static void ConfigureFeatureChecker(ServiceConfigurationContext context)
    {
        context.Services.Configure<FeatureCheckerClientOptions>(context.Configuration.GetSection("RemoteFeatureChecker"));
        context.Services.AddHttpClient(nameof(HttpFeatureCheckerClient));
        context.Services.AddHttpClient(nameof(RemoteFeatureAccessTokenProvider));
        context.Services.TryAddSingleton<IRemoteFeatureAccessTokenProvider, RemoteFeatureAccessTokenProvider>();
        context.Services.TryAddSingleton<DefaultFeatureCheckerClient>();
        context.Services.TryAddTransient<EfCoreFeatureCheckerClient>();
        context.Services.TryAddTransient<HttpFeatureCheckerClient>();

        context.Services.RemoveAll<IFeatureCheckerClient>();
        context.Services.AddTransient<IFeatureCheckerClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<FeatureCheckerClientOptions>>().Value;
            return options.Enabled ? sp.GetRequiredService<HttpFeatureCheckerClient>() : sp.GetRequiredService<EfCoreFeatureCheckerClient>();
        });
    }

    private static void ConfigureIdentityPermissionTargetValidation(ServiceConfigurationContext context)
    {
        context.Services.Configure<IdentityPermissionTargetValidationOptions>(
            context.Configuration.GetSection("IdentityPermissionTargetValidation"));
        context.Services.AddHttpClient(nameof(PermissionIdentityInternalAccessTokenProvider));
        context.Services.AddHttpClient(IdentityPermissionTargetValidator.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<IdentityPermissionTargetValidationOptions>>().Value;
            if (Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var baseAddress))
            {
                client.BaseAddress = baseAddress;
            }

            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 30));
        });
        context.Services.TryAddTransient<IIdentityPermissionTargetValidator, IdentityPermissionTargetValidator>();
    }

    private static void ConfigurePermissionCacheRefresh(ServiceConfigurationContext context)
    {
        context.Services.Configure<PermissionCacheRefreshOptions>(
            context.Configuration.GetSection(PermissionCacheRefreshOptions.SectionName));
        context.Services.PostConfigure<PermissionCacheRefreshOptions>(PermissionCacheRefreshOptionsPostConfigure.Normalize);
        context.Services.AddBackgroundJob<PermissionCacheRefreshBackgroundJob, PermissionCacheRefreshJobArgs>();
        context.Services.AddBackgroundJob<PermissionImportApplyBackgroundJob, PermissionImportApplyJobArgs>();
    }
}
