using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.BackgroundTasks.Jobs;
using Shared.BackgroundTasks.Workers;
using Shared.Modularity.DependencyInjection;

namespace Shared.BackgroundTasks.DependencyInjection;

public static class BackgroundTasksServiceCollectionExtensions
{
    public static IServiceCollection AddBackgroundJob<TJob, TArgs>(
        this IServiceCollection services,
        string? jobName = null)
        where TJob : class, IBackgroundJob<TArgs>
    {
        // Background jobs are resolved and invoked by reflection in BackgroundJobExecutor, which reads
        // any [UnitOfWork] attribute on ExecuteAsync and opens the unit of work itself. The job must
        // therefore never be wrapped in a Castle interception proxy: doing so is redundant (the executor
        // already applies the attribute) and fails outright for internal or sealed job classes, which the
        // Autofac conventional registrar would otherwise try to class-proxy.
        DynamicProxyIgnoreTypes.Add<TJob>();

        services.AddTransient<TJob>();
        services.Configure<BackgroundTasksOptions>(options =>
        {
            options.Jobs.Add(BackgroundJobDefinition.Create<TJob, TArgs>(jobName));
        });

        return services;
    }

    public static IServiceCollection AddSharedPeriodicWorker<TWorker, TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName,
        Action<TOptions>? configure = null)
        where TWorker : class, IPeriodicBackgroundWorker
        where TOptions : BackgroundWorkerOptions, new()
    {
        services.AddScoped<TWorker>();
        services.Configure<TOptions>(configuration.GetSection(sectionName));
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.PostConfigure<TOptions>(BackgroundWorkerOptionsPostConfigure.Normalize);
        services.AddHostedService<SharedPeriodicBackgroundService<TWorker, TOptions>>();
        return services;
    }

    public static IServiceCollection AddSharedPeriodicWorker<TWorker, TOptions>(
        this IServiceCollection services,
        Action<TOptions>? configure = null)
        where TWorker : class, IPeriodicBackgroundWorker
        where TOptions : BackgroundWorkerOptions, new()
    {
        services.AddScoped<TWorker>();
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.PostConfigure<TOptions>(BackgroundWorkerOptionsPostConfigure.Normalize);
        services.AddHostedService<SharedPeriodicBackgroundService<TWorker, TOptions>>();
        return services;
    }
}
