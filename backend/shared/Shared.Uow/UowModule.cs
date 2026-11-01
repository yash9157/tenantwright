using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Shared.Modularity.Modules;
using Shared.Modularity.Context;
using Shared.Uow.Abstractions;
using Shared.Uow.AspNetCore;
using Shared.Uow.Context;
using Microsoft.Extensions.DependencyInjection;
using Shared.Uow.Options;
using Shared.Uow.Implementations;
using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shared.Uow;

public class UowModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        context.Services.AddScoped<IUnitOfWorkSuppressionContext, UnitOfWorkSuppressionContext>();
        context.Services.TryAddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
        context.Services.TryAddTransient<UnitOfWorkRequestExecutor>();
        context.Services.TryAddTransient<UnitOfWorkActionFilter>();
        context.Services.TryAddTransient<UnitOfWorkPageFilter>();
        context.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<MvcOptions>, UnitOfWorkFilterMvcOptionsSetup>());
        context.Services.OnServiceRegistered(UnitOfWorkInterceptorRegistrar.RegisterIfNeeded);

        context.Services.Configure<UnitOfWorkDefaultOptions>(
            context.Configuration.GetSection(UnitOfWorkDefaultOptions.SectionName));
        context.Services.PostConfigure<UnitOfWorkDefaultOptions>(options =>
        {
            if (options.IsolationLevel.HasValue && options.TransactionBehavior == UnitOfWorkTransactionBehavior.Disabled)
            {
                throw new InvalidOperationException(
                    "UnitOfWork default isolation level can not be set when transaction behavior is disabled.");
            }
        });

        context.Services.Configure<UnitOfWorkMiddlewareOptions>(
            context.Configuration.GetSection(UnitOfWorkMiddlewareOptions.SectionName));
        context.Services.PostConfigure<UnitOfWorkMiddlewareOptions>(options =>
        {
            if (!options.IgnoredUrls.Contains("/health", StringComparer.OrdinalIgnoreCase))
            {
                options.IgnoredUrls.Add("/health");
            }

            if (!options.IgnoredUrls.Contains("/alive", StringComparer.OrdinalIgnoreCase))
            {
                options.IgnoredUrls.Add("/alive");
            }

            var normalized = options.IgnoredUrls
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            options.IgnoredUrls.Clear();
            options.IgnoredUrls.AddRange(normalized);
        });
    }
}
