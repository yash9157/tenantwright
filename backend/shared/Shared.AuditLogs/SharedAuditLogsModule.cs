using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.AuditLogs.Publishing;
using Shared.CurrentUser;
using Shared.EventBus.Messaging;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.MultiTenancy;

namespace Shared.AuditLogs;

[DependsOn(typeof(CurrentUserModule))]
[DependsOn(typeof(MultiTenancyModule))]
public sealed class SharedAuditLogsModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.OnServiceRegistered(AuditingInterceptorRegistrar.RegisterIfNeeded);

        context.Services.Configure<SharedAuditingOptions>(
            context.Configuration.GetSection(SharedAuditingOptions.SectionName));
        context.Services.PostConfigure<SharedAuditingOptions>(SharedAuditingOptionsPostConfigure.Normalize);

        context.Services.Configure<SharedAspNetCoreAuditingOptions>(
            context.Configuration.GetSection(SharedAuditingOptions.SectionName));
        context.Services.PostConfigure<SharedAspNetCoreAuditingOptions>(options =>
        {
            var normalized = options.IgnoredUrls
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalized.Count == 0)
            {
                normalized.Add("/health");
                normalized.Add("/alive");
                normalized.Add("/metrics");
                normalized.Add("/connect/token");
                normalized.Add("/connect/authorize");
                normalized.Add("/connect/logout");
                normalized.Add("/connect/revoke");
            }

            if (!normalized.Contains("/alive", StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add("/alive");
            }

            options.IgnoredUrls.Clear();
            foreach (var ignoredUrl in normalized)
            {
                options.IgnoredUrls.Add(ignoredUrl);
            }
        });

        context.Services.Configure<AuditRedactionOptions>(
            context.Configuration.GetSection(AuditRedactionOptions.SectionPath));
        context.Services.PostConfigure<AuditRedactionOptions>(AuditRedactionOptionsPostConfigure.Normalize);
        context.Services.Configure<AuditLogSignatureOptions>(
            context.Configuration.GetSection(AuditLogSignatureOptions.SectionName));

        context.Services.TryAddTransient<SharedAuditActionFilter>();
        context.Services.TryAddTransient<SharedAuditPageFilter>();
        context.Services.TryAddTransient<IAuditPolicyEvaluator, AuditPolicyEvaluator>();
        context.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<MvcOptions>, SharedAuditingFilterMvcOptionsSetup>());

        RegisterAuditPipeline(
            context.Services,
            TenantWrightMessagingProviderConfiguration.Read(context.Configuration));
    }

    /// <summary>
    /// A host with a real messaging provider writes audit records durably as audit write commands. A bus-free host
    /// (<c>Messaging:Provider=None</c>, the default when unset) has no broker to carry them, so both the auditing store
    /// and direct audit publishers record through the structured log store instead of failing every audited request.
    /// </summary>
    private static void RegisterAuditPipeline(IServiceCollection services, TenantWrightMessagingProvider provider)
    {
        // The stores and the durable publisher self-register via conventional scanning (ITransientDependency /
        // ISingletonDependency), which runs before this module. Replace (not TryAdd) deterministically wins over
        // whichever the scanner added, instead of racing it.
        if (provider == TenantWrightMessagingProvider.None)
        {
            services.Replace(ServiceDescriptor.Singleton<IAuditingStore, SimpleLogAuditingStore>());
            services.Replace(ServiceDescriptor.Transient<IAuditLogWriteRequestPublisher, LogOnlyAuditLogWriteRequestPublisher>());
            return;
        }

        services.Replace(ServiceDescriptor.Transient<IAuditingStore, EventBusAuditingStore>());
        services.TryAddTransient<IAuditLogWriteRequestPublisher, AuditLogWriteRequestPublisher>();
    }
}
