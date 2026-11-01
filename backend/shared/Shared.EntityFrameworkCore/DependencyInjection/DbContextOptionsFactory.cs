using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Data;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore.GlobalFilters;
using Shared.MultiTenancy;
using System.Threading.Tasks;

namespace Shared.EntityFrameworkCore.DependencyInjection;

public static class DbContextOptionsFactory
{
    public static async Task<DbContextOptions<TDbContext>> CreateAsync<TDbContext>(IServiceProvider serviceProvider)
        where TDbContext : SharedDbContext<TDbContext>
    {
        var creationContext = await GetCreationContextAsync<TDbContext>(serviceProvider);

        var context = new SharedDbContextConfigurationContext<TDbContext>(
            creationContext.ConnectionString,
            serviceProvider,
            creationContext.ConnectionStringName,
            creationContext.ExistingConnection
        );

        var options = GetDbContextOptions<TDbContext>(serviceProvider);

        PreConfigure(options, context);
        Configure(options, context);

        if (serviceProvider.GetRequiredService<IOptions<SharedEfCoreGlobalFilterOptions>>().Value.UseDbFunction)
        {
            context.DbContextOptions.AddSharedDbContextOptionsExtension();
        }

        return context.DbContextOptions.Options;
    }

    private static void PreConfigure<TDbContext>(
        SharedDbContextOptions options,
        SharedDbContextConfigurationContext<TDbContext> context)
        where TDbContext : SharedDbContext<TDbContext>
    {
        foreach (var defaultPreConfigureAction in options.DefaultPreConfigureActions)
        {
            defaultPreConfigureAction.Invoke(context);
        }

        var preConfigureActions = options.PreConfigureActions.GetOrDefault(typeof(TDbContext));
        if (preConfigureActions is { Count: > 0 })
        {
            foreach (var preConfigureAction in preConfigureActions)
            {
                ((Action<SharedDbContextConfigurationContext<TDbContext>>)preConfigureAction).Invoke(context);
            }
        }
    }

    private static void Configure<TDbContext>(
        SharedDbContextOptions options,
        SharedDbContextConfigurationContext<TDbContext> context)
        where TDbContext : SharedDbContext<TDbContext>
    {
        var configureAction = options.ConfigureActions.GetOrDefault(typeof(TDbContext));
        if (configureAction != null)
        {
            ((Action<SharedDbContextConfigurationContext<TDbContext>>)configureAction).Invoke(context);
        }
        else if (options.DefaultConfigureAction != null)
        {
            options.DefaultConfigureAction.Invoke(context);
        }
        else
        {
            throw new InvalidOperationException(
                $"No configuration found for {typeof(TDbContext).AssemblyQualifiedName}! Use services.Configure<SharedDbContextOptions>(...) to configure it.");
        }
    }

    private static SharedDbContextOptions GetDbContextOptions<TDbContext>(IServiceProvider serviceProvider)
        where TDbContext : SharedDbContext<TDbContext>
    {
        return serviceProvider.GetRequiredService<IOptions<SharedDbContextOptions>>().Value;
    }

    private static async Task<DbContextCreationContext> GetCreationContextAsync<TDbContext>(IServiceProvider serviceProvider)
        where TDbContext : SharedDbContext<TDbContext>
    {
        var context = DbContextCreationContext.Current;
        if (context != null)
        {
            return context;
        }

        var connectionStringName = ConnectionStringNameAttribute.GetConnStringName<TDbContext>();
        var connectionString = await ResolveConnectionStringAsync<TDbContext>(serviceProvider, connectionStringName);

        return new DbContextCreationContext(
            connectionStringName,
            connectionString
        );
    }

    private static async Task<string> ResolveConnectionStringAsync<TDbContext>(
        IServiceProvider serviceProvider,
        string connectionStringName)
    {
        var connectionStringResolver = serviceProvider.GetRequiredService<IConnectionStringResolver>();
        var currentTenant = serviceProvider.GetRequiredService<ICurrentTenant>();

        // Multi-tenancy unaware contexts should always use the host connection string
        if (typeof(TDbContext).IsDefined(typeof(IgnoreMultiTenancyAttribute), false))
        {
            using (currentTenant.Change(null))
            {
                return await connectionStringResolver.ResolveAsync(connectionStringName);
            }
        }

        return await connectionStringResolver.ResolveAsync(connectionStringName);
    }
}
