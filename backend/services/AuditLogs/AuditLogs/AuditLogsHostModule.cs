using AuditLogs.Application;
using AuditLogs.Domain;
using AuditLogs.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using OpenIddict.Validation.AspNetCore;
using Shared.AspNetCore.Host;
using Shared.AspNetCore.Host.Configuration;
using Shared.AspNetCore.Host.Middleware;
using Shared.AuditLogs;
using Shared.AuditLogs.AspNetCore;
using Shared.BackgroundTasks;
using Shared.BackgroundTasks.DependencyInjection;
using Shared.BackgroundTasks.Hangfire;
using Shared.BackgroundTasks.Jobs;
using Shared.Data;
using Shared.Data.Seeding;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.Uow.Implementations;
using Shared.Permissions;
using System;
using Shared.Permissions.Features;
using Permissions.Application.Contracts.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AuditLogs.Application.Events;
using AuditLogs.Application.Auditing;
using AuditLogs.Workers;
using Microsoft.Extensions.Options;
using AuditLogs.Health;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EventBus.Hosting;
using Shared.EventBus.Messaging;

namespace AuditLogs;

[DependsOn(typeof(AuditLogsDomainModule))]
[DependsOn(typeof(AuditLogsApplicationModule))]
[DependsOn(typeof(AuditLogsEntityFrameworkCoreModule))]
[DependsOn(typeof(SharedAspNetCoreHostModule))]
[DependsOn(typeof(SharedAuditLogsModule))]
[DependsOn(typeof(SharedBackgroundTasksModule))]
[DependsOn(typeof(SharedBackgroundTasksHangfireModule))]
[DependsOn(typeof(SharedDataModule))]
[DependsOn(typeof(SharedPermissionsModule))]
public class AuditLogsHostModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;
        var configuration = context.Configuration;
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        // Configure Feature Checker (HTTP Client)
        services.Configure<FeatureCheckerClientOptions>(configuration.GetSection("RemoteFeatureChecker"));
        services.AddHttpClient(nameof(HttpFeatureCheckerClient));
        services.TryAddTransient<IFeatureCheckerClient, HttpFeatureCheckerClient>();
        services.TryAddTransient<AuditLogWriteRequestedEventHandler>();

        services.AddTenantWrightMessagingHost<AuditLogsDbContextApp>(
            configuration,
            TenantWrightMessagingTopology.AuditLogsPrincipal);

        if (!isDevelopment)
        {
            var authority = configuration["AuthServer:Authority"];
            if (string.IsNullOrWhiteSpace(authority) || !authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-auditlogs",
                    key: "AuthServer:Authority",
                    rule: "Must be a non-empty https URL outside Development.",
                    fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-auditlogs/values.yaml to an https URL and redeploy.");
            }

            var connectionString = configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-auditlogs",
                    key: "ConnectionStrings:Default",
                    rule: "Default connection string is required outside Development.",
                    fixHint: "Set secret ref 'ConnectionStrings__Default' to 'tenantwright-auditlogs-db.connection-string' in charts/tenantwright-auditlogs/values.yaml and ensure the secret exists.");
            }
        }
        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        services.AddOpenIddict()
            .AddValidation(options =>
            {
                var issuer = configuration["AuthServer:Authority"] ?? (isDevelopment ? "http://localhost:5234/" : null);
                if (string.IsNullOrWhiteSpace(issuer))
                {
                    throw StartupConfigExceptionFactory.InvalidConfiguration(
                        service: "tenantwright-auditlogs",
                        key: "AuthServer:Authority",
                        rule: "Authority is required to configure OpenIddict validation outside Development.",
                        fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-auditlogs/values.yaml and redeploy.");
                }
                options.SetIssuer(new Uri(issuer));
                options.AddAudiences("auditlogs");
                options.UseSystemNetHttp();
                options.UseAspNetCore();
            });

        services.AddAuthorization();

        services.Configure<SharedDataSeedOptions>(_ =>
        {
        });

        services.Configure<AuditLogSealOptions>(configuration.GetSection(AuditLogSealOptions.SectionName));
        services.PostConfigure<AuditLogSealOptions>(AuditLogSealOptionsPostConfigure.Normalize);
        services.AddOptions<AuditLogsOperationalHealthOptions>()
            .Bind(configuration.GetSection(AuditLogsOperationalHealthOptions.SectionName))
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 30, "Operational health timeout must be between 1 and 30 seconds.")
            .Validate(options => options.HangfireHeartbeatGraceSeconds is >= 30 and <= 600, "Hangfire heartbeat grace must be between 30 and 600 seconds.")
            .Validate(options => options.MaximumEligibleSealLagRows >= 0, "Maximum seal lag cannot be negative.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.BlobCanaryPrefix), "Blob canary prefix is required.")
            .ValidateOnStart();
        var healthTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>($"{AuditLogsOperationalHealthOptions.SectionName}:TimeoutSeconds") ?? 5,
            1,
            30));
        services.AddHealthChecks()
            .AddCheck<AuditLogsHangfireHealthCheck>("AuditLogs Hangfire", tags: ["deep"], timeout: healthTimeout)
            .AddCheck<AuditLogsSealHealthCheck>("AuditLogs sealing", tags: ["deep"], timeout: healthTimeout)
            .AddCheck<AuditLogsArchiveBlobHealthCheck>("AuditLogs archive blob", tags: ["deep"], timeout: healthTimeout);
        var sealWorkerOptions = AuditLogSealWorkerOptions.FromSealOptions(
            configuration.GetSection(AuditLogSealOptions.SectionName).Get<AuditLogSealOptions>()
                ?? new AuditLogSealOptions());
        services.AddSharedPeriodicWorker<AuditLogSealWorker, AuditLogSealWorkerOptions>(
            options =>
            {
                options.Enabled = sealWorkerOptions.Enabled;
                options.WorkerName = sealWorkerOptions.WorkerName;
                options.PollIntervalMs = sealWorkerOptions.PollIntervalMs;
                options.ErrorDelayMs = sealWorkerOptions.ErrorDelayMs;
                options.DistributedLockName = sealWorkerOptions.DistributedLockName;
                options.OwnershipAcquireTimeoutMs = sealWorkerOptions.OwnershipAcquireTimeoutMs;
                options.OwnershipLeaseMs = sealWorkerOptions.OwnershipLeaseMs;
            });
    }

    public override async Task OnApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        var app = context.ApplicationBuilder;
        var env = context.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();

        using (var scope = context.ServiceProvider.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<IDataSeeder>();
            await seeder.SeedAsync();
        }

        await ConfigureArchiveRecurringJobAsync(context.ServiceProvider);

        if (SharedAspNetCoreHostModule.IsHttpsRedirectionEnabled(configuration))
        {
            app.UseHttpsRedirection();
        }
        app.UseRouting();
        app.UseUnitOfWork();
        app.UseAuditLogCapture();
        app.UseAuthentication();
        app.UseMiddleware<SessionValidationMiddleware>();
        app.UseMiddleware<MultiTenancyMiddleware>();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }

    private static async Task ConfigureArchiveRecurringJobAsync(IServiceProvider serviceProvider)
    {
        var archiveOptions = serviceProvider
            .GetRequiredService<IOptions<AuditLogArchiveOptions>>()
            .Value;
        var recurringJobs = serviceProvider.GetRequiredService<IRecurringBackgroundJobManager>();
        const string recurringJobId = "auditlogs.archive-maintenance";

        if (!archiveOptions.Enabled)
        {
            await recurringJobs.RemoveIfExistsAsync(recurringJobId);
            return;
        }

        await recurringJobs.AddOrUpdateAsync(
            recurringJobId,
            new AuditLogArchiveMaintenanceJobArgs(),
            archiveOptions.RecurringCron,
            new RecurringBackgroundJobOptions
            {
                Queue = "auditlogs-recurring",
                TimeZone = TimeZoneInfo.Utc
            });
    }
}
