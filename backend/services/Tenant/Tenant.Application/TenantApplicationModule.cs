using Tenant.Application.Contracts;
using Tenant.Domain;
using Tenant.EntityFrameworkCore;
using Shared.Caching;
using Shared.DistributedLocking;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Shared.Permissions.Features;
using Tenant.Application.Tenants;

namespace Tenant.Application;

[DependsOn(typeof(TenantDomainModule))]
[DependsOn(typeof(TenantApplicationContractsModule))]
[DependsOn(typeof(TenantEntityFrameworkCoreModule))]
[DependsOn(typeof(CachingModule))]
[DependsOn(typeof(DistributedLockingModule))]
public class TenantApplicationModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.Configure<TenantImpactEnrichmentOptions>(
            context.Configuration.GetSection("TenantImpactEnrichment"));
        context.Services.AddTenantSharedDatabaseTargets(context.Configuration);
        context.Services.AddHttpClient(nameof(InternalAccessTokenProvider));
        context.Services.TryAddSingleton<IInternalAccessTokenProvider, InternalAccessTokenProvider>();

        context.Services.AddHttpClient(TenantImpactEnrichmentClient.IdentityHttpClientName, (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<TenantImpactEnrichmentOptions>>().Value;
            if (Uri.TryCreate(options.IdentityBaseAddress, UriKind.Absolute, out var baseAddress))
            {
                client.BaseAddress = baseAddress;
            }

            client.Timeout = TimeSpan.FromMilliseconds(Math.Clamp(options.TimeoutMilliseconds, 250, 5000));
        });
        context.Services.AddHttpClient(TenantImpactEnrichmentClient.NotificationsHttpClientName, (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<TenantImpactEnrichmentOptions>>().Value;
            if (Uri.TryCreate(options.NotificationsBaseAddress, UriKind.Absolute, out var baseAddress))
            {
                client.BaseAddress = baseAddress;
            }

            client.Timeout = TimeSpan.FromMilliseconds(Math.Clamp(options.TimeoutMilliseconds, 250, 5000));
        });
        context.Services.TryAddTransient<TenantImpactEnrichmentClient>();
    }
}
