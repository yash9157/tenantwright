using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Modularity.Modules;
using Shared.Modularity.Context;
using Shared.Modularity.Extensions;
using Shared.Uow;
using Shared.Data.Filtering;
using Shared.Data.ConnectionStrings;
using Shared.Data.Seeding;
using Shared.Modularity.DependencyInjection;

namespace Shared.Data;

[DependsOn(
    typeof(UowModule)
)]
public class SharedDataModule : BaseModule
{
    private readonly HashSet<Type> _autoSeedContributorTypes = [];

    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.OnExposing(exposingContext =>
        {
            if (!typeof(IDataSeedContributor).IsAssignableFrom(exposingContext.ImplementationType))
            {
                return;
            }

            _autoSeedContributorTypes.Add(exposingContext.ImplementationType);

            exposingContext.ExposedTypes.RemoveAll(service => service.ServiceType == typeof(IDataSeedContributor));

            var selfIdentifier = new ServiceIdentifier(exposingContext.ImplementationType);
            if (!exposingContext.ExposedTypes.Contains(selfIdentifier))
            {
                exposingContext.ExposedTypes.Add(selfIdentifier);
            }
        });
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.Configure<SharedDbConnectionOptions>(context.Configuration);
        context.Services.Configure<SharedConnectionStringResolverOptions>(
            context.Configuration.GetSection(SharedConnectionStringResolverOptions.SectionName));
        context.Services.Configure<SharedDataSeedOptions>(options =>
        {
            foreach (var contributorType in _autoSeedContributorTypes.OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                options.Contributors.AddIfNotContains(contributorType);
            }
        });

        context.Services.AddSingleton(typeof(IDataFilter<>), typeof(DataFilter<>));
    }

    public override void PostConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.Configure<SharedDbConnectionOptions>(options =>
        {
            options.Databases.RefreshIndexes();
        });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var logger = context.ServiceProvider.GetService<ILogger<SharedDataModule>>();
        if (logger == null)
        {
            return;
        }

        var resolverOptions = context.ServiceProvider
            .GetService<Microsoft.Extensions.Options.IOptions<SharedConnectionStringResolverOptions>>()
            ?.Value ?? new SharedConnectionStringResolverOptions();

        logger.LogInformation(
            "Shared.Data startup summary: AutoSeedContributorCount={AutoSeedContributorCount}, StrictConnectionResolver={StrictConnectionResolver}.",
            _autoSeedContributorTypes.Count,
            resolverOptions.ThrowOnMissing);
    }
}
