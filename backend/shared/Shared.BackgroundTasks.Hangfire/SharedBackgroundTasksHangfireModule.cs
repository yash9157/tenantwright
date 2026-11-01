using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shared.BackgroundTasks.Jobs;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;

namespace Shared.BackgroundTasks.Hangfire;

[DependsOn(typeof(SharedBackgroundTasksModule))]
public sealed class SharedBackgroundTasksHangfireModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Configuration;
        var enabled = configuration.GetValue<bool?>($"{BackgroundTasksHangfireOptions.SectionName}:Enabled") ?? false;

        context.Services.Configure<BackgroundTasksHangfireOptions>(
            configuration.GetSection(BackgroundTasksHangfireOptions.SectionName));
        context.Services.PostConfigure<BackgroundTasksHangfireOptions>(options =>
        {
            var isDevelopment = string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                Environments.Development,
                StringComparison.OrdinalIgnoreCase);
            BackgroundTasksHangfireOptionsPostConfigure.Normalize(options, isDevelopment);
        });

        if (!enabled)
        {
            return;
        }

        context.Services.TryAddSingleton<IBackgroundJobEnvelopeFactory, BackgroundJobEnvelopeFactory>();
        context.Services.Replace(ServiceDescriptor.Transient<IBackgroundJobManager, HangfireBackgroundJobManager>());
        context.Services.Replace(ServiceDescriptor.Transient<IRecurringBackgroundJobManager, HangfireRecurringBackgroundJobManager>());
        context.Services.TryAddTransient<HangfireBackgroundJobDispatcher>();

        context.Services.AddHangfire((serviceProvider, globalConfiguration) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<BackgroundTasksHangfireOptions>>().Value;
            options.Validate(configuration);

            var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"BackgroundTasks:Hangfire is enabled but connection string '{options.ConnectionStringName}' is missing.");
            }

            globalConfiguration
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(
                    postgres => postgres.UseNpgsqlConnection(connectionString),
                    new PostgreSqlStorageOptions
                    {
                        SchemaName = options.SchemaName,
                        PrepareSchemaIfNecessary = options.PrepareSchemaIfNecessary.GetValueOrDefault()
                    });
        });

        context.Services.AddHangfireServer((serviceProvider, options) =>
        {
            var moduleOptions = serviceProvider
                .GetRequiredService<IOptions<BackgroundTasksHangfireOptions>>()
                .Value;
            moduleOptions.Validate(configuration);

            options.ServerName = $"{moduleOptions.ServiceName}:{Environment.MachineName}";
            options.Queues = moduleOptions.Queues.ToArray();
            options.WorkerCount = moduleOptions.WorkerCount;
        });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var options = context.ServiceProvider.GetRequiredService<IOptions<BackgroundTasksHangfireOptions>>().Value;
        if (!options.Enabled || !options.Dashboard.Enabled)
        {
            return;
        }

        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();
        options.Validate(configuration);

        context.ApplicationBuilder.UseHangfireDashboard(
            options.Dashboard.Path,
            new DashboardOptions
            {
                Authorization =
                [
                    new InternalHangfireDashboardAuthorizationFilter(
                        options.Dashboard.AllowedRoles,
                        options.Dashboard.AllowedClientIds)
                ]
            });
    }
}
