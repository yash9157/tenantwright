using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Caching;
using Shared.Caching.Abstractions;
using Shared.MultiTenancy.Caching;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.Security;
using System.Diagnostics;
using System.Linq;

namespace Shared.MultiTenancy;

[DependsOn(typeof(SharedSecurityModule))]
[DependsOn(typeof(CachingModule))]
public class MultiTenancyModule : BaseModule
{
    private long _configureDurationMs;

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var timer = Stopwatch.StartNew();

        context.Services.Configure<SharedTenantResolveOptions>(
            context.Configuration.GetSection(SharedTenantResolveOptions.SectionName));
        context.Services.PostConfigure<SharedTenantResolveOptions>(options =>
        {
            options.HeaderKeys.Clear();
            options.HeaderKeys.Add("X-Tenant-Id");

            options.Resolvers.Clear();
            options.Resolvers.Add(typeof(CurrentUserTenantResolveContributor));
            options.Resolvers.Add(typeof(HeaderTenantResolveContributor));
        });

        context.Services.TryAddTransient<CurrentUserTenantResolveContributor>();
        context.Services.TryAddTransient<HeaderTenantResolveContributor>();

        context.Services.Replace(ServiceDescriptor.Singleton<ITenantResolveResultAccessor, AsyncLocalTenantResolveResultAccessor>());
        context.Services.Replace(ServiceDescriptor.Transient<ITenantResolver, TenantResolver>());

        context.Services.Replace(ServiceDescriptor.Singleton<ICacheTenantResolver, CurrentTenantCacheTenantResolver>());
        context.Services.Replace(ServiceDescriptor.Singleton<IDistributedCache<TenantCacheItem, string>, TenantByIdCache>());
        context.Services.Replace(ServiceDescriptor.Singleton<IDistributedCache<TenantByNameCacheItem, string>, TenantByNameCache>());
        context.Services.Replace(ServiceDescriptor.Singleton<IDistributedCache<TenantListCacheItem, string>, TenantListCache>());
        context.Services.Replace(ServiceDescriptor.Singleton<IDistributedCache<TenantListStampCacheItem, string>, TenantListStampCache>());

        timer.Stop();
        _configureDurationMs = timer.ElapsedMilliseconds;
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        ValidateSingletonRegistration<ICacheTenantResolver>(context);
        ValidateSingletonRegistration<ITenantStore>(context);
        ValidateSingletonRegistration<ITenantResolver>(context);

        var logger = context.ServiceProvider.GetService<ILogger<MultiTenancyModule>>();
        var options = context.ServiceProvider.GetRequiredService<IOptions<SharedTenantResolveOptions>>().Value;
        if (logger == null)
        {
            return;
        }

        logger.LogInformation(
            "Shared.MultiTenancy startup summary: ResolverCount={ResolverCount}, ResolverOrder={ResolverOrder}, FailOnInvalidTenantInput={FailOnInvalidTenantInput}, FailOnAmbiguousTenantInput={FailOnAmbiguousTenantInput}, ConfigureDurationMs={ConfigureDurationMs}.",
            options.Resolvers.Count,
            string.Join(" -> ", options.Resolvers.Select(type => type.Name)),
            options.FailOnInvalidTenantInput,
            options.FailOnAmbiguousTenantInput,
            _configureDurationMs);
    }

    private static void ValidateSingletonRegistration<TService>(ApplicationInitializationContext context)
    {
        var registrations = context.ServiceProvider.GetServices<TService>().ToArray();
        if (registrations.Length == 1)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected exactly one registration for '{typeof(TService).FullName}', but found {registrations.Length}.");
    }
}
