using Notifications.Application;
using Notifications.Application.Contracts.Presence;
using Notifications.Application.Contracts.Realtime;
using Notifications.Domain;
using Notifications.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenIddict.Validation.AspNetCore;
using OpenIddict.Abstractions;
using Shared.AspNetCore.Host;
using Shared.AspNetCore.Host.Configuration;
using Shared.AspNetCore.Host.Middleware;
using Shared.AuditLogs;
using Shared.AuditLogs.AspNetCore;
using Shared.Data;
using Shared.Data.Seeding;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.Uow.Implementations;
using Shared.EventBus.Hosting;
using Shared.EventBus.Messaging;
using Shared.Permissions;
using System;
using System.Linq;
using Shared.Permissions.Features;
using Permissions.Application.Contracts.Features;
using Notifications.Hubs;
using Notifications.Presence;
using Notifications.Events;
using Notifications.Application.Delivery;
using Notifications.Application.Delivery.Providers;
using Notifications.Application.RateLimiting;
using Notifications.Application.Retention;
using Notifications.Controllers.Webhooks;
using Notifications.Data;
using Notifications.Delivery;
using Notifications.Realtime;
using Notifications.Application.Digests;
using Notifications.Application.ProviderConfig.Security;
using Notifications.Workers;
using Shared.BackgroundTasks;
using Shared.BackgroundTasks.DependencyInjection;
using Shared.BackgroundTasks.Workers;
using Shared.Caching;
using StackExchange.Redis;

namespace Notifications;

[DependsOn(typeof(NotificationsDomainModule))]
[DependsOn(typeof(NotificationsApplicationModule))]
[DependsOn(typeof(NotificationsEntityFrameworkCoreModule))]
[DependsOn(typeof(SharedAspNetCoreHostModule))]
[DependsOn(typeof(SharedAuditLogsModule))]
[DependsOn(typeof(SharedBackgroundTasksModule))]
[DependsOn(typeof(SharedDataModule))]
[DependsOn(typeof(SharedPermissionsModule))]
public class NotificationsHostModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;
        var configuration = context.Configuration;
        services.AddTenantWrightMessagingHost<global::Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp>(
            configuration,
            TenantWrightMessagingTopology.NotificationsPrincipal);
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        services.Configure<FeatureCheckerClientOptions>(configuration.GetSection("RemoteFeatureChecker"));
        services.AddHttpClient(nameof(HttpFeatureCheckerClient));
        services.TryAddTransient<IFeatureCheckerClient, HttpFeatureCheckerClient>();

        if (!isDevelopment)
        {
            var authority = configuration["AuthServer:Authority"];
            if (string.IsNullOrWhiteSpace(authority) || !authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-notifications",
                    key: "AuthServer:Authority",
                    rule: "Must be a non-empty https URL outside Development.",
                    fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-notifications/values.yaml to an https URL and redeploy.");
            }

            var connectionString = configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-notifications",
                    key: "ConnectionStrings:Default",
                    rule: "Default connection string is required outside Development.",
                    fixHint: "Set secret ref 'ConnectionStrings__Default' to 'tenantwright-notifications-db.connection-string' in charts/tenantwright-notifications/values.yaml and ensure the secret exists.");
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
                        service: "tenantwright-notifications",
                        key: "AuthServer:Authority",
                        rule: "Authority is required to configure OpenIddict validation outside Development.",
                        fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-notifications/values.yaml and redeploy.");
                }
                options.SetIssuer(new Uri(issuer));
                options.AddAudiences("notifications");
                options.UseSystemNetHttp();
                options.UseAspNetCore();
            });

        services.AddAuthorization(options =>
        {
            var allowedInternalClients = (configuration["NotificationsInternal:AllowedClientIds"]
                ?? configuration["OpenIddict:Clients:Internal:ClientId"]
                ?? "auth-internal")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);

            options.AddPolicy("NotificationsInternal", policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var scope = context.User.FindFirst(OpenIddictConstants.Claims.Scope)?.Value;
                    var clientId = context.User.FindFirst(OpenIddictConstants.Claims.ClientId)?.Value;
                    var subject = context.User.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
                    return !string.IsNullOrWhiteSpace(scope) &&
                           scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("notifications-internal", StringComparer.Ordinal) &&
                           !string.IsNullOrWhiteSpace(clientId) &&
                           allowedInternalClients.Contains(clientId) &&
                           string.Equals(subject, clientId, StringComparison.Ordinal);
                });
            });
        });

        services.Configure<NotificationsRealtimeOptions>(configuration.GetSection("Notifications:Realtime"));
        services.Configure<NotificationsPresenceOptions>(configuration.GetSection("Notifications:Presence"));
        services.Configure<NotificationsDeliveryOptions>(configuration.GetSection("Notifications:Delivery"));
        services.Configure<NotificationsDeliveryLatencyRollupOptions>(
            configuration.GetSection("Notifications:DeliveryLatencyRollup"));
        services.TryAddTransient<INotificationsPresenceStore, DistributedNotificationsPresenceStore>();

        var deliveryOptions = configuration
            .GetSection("Notifications:Delivery")
            .Get<NotificationsDeliveryOptions>() ?? new NotificationsDeliveryOptions();
        services.AddSharedPeriodicWorker<NotificationDeliveryBackgroundWorker, NotificationDeliveryBackgroundWorkerOptions>(
            configuration,
            "Notifications:Delivery",
            options =>
            {
                options.WorkerName = NotificationDeliveryBackgroundWorker.WorkerName;
                options.DistributedLockName = NotificationDeliveryBackgroundWorker.OwnershipLockName;
                options.ErrorDelayMs = Math.Clamp(deliveryOptions.PollIntervalMs, 250, 60000);
                options.OwnershipAcquireTimeoutMs = Math.Clamp(deliveryOptions.OwnershipAcquireTimeoutMs, 1, 5000);
                options.OwnershipLeaseMs = Math.Clamp(deliveryOptions.OwnershipLeaseMs, 500, 120000);
            });
        services.TryAddTransient<NotificationSendRequestedEventHandler>();
        services.TryAddTransient<TenantClosureChangedEventHandler>();

        var latencyRollupOptions = configuration
            .GetSection("Notifications:DeliveryLatencyRollup")
            .Get<NotificationsDeliveryLatencyRollupOptions>() ?? new NotificationsDeliveryLatencyRollupOptions();
        services.AddSharedPeriodicWorker<NotificationDeliveryLatencyRollupBackgroundWorker, BackgroundWorkerOptions>(
            options =>
            {
                var intervalMs = checked((int)TimeSpan.FromSeconds(Math.Clamp(latencyRollupOptions.IntervalSeconds, 30, 3600)).TotalMilliseconds);
                options.Enabled = latencyRollupOptions.Enabled;
                options.WorkerName = NotificationDeliveryLatencyRollupBackgroundWorker.WorkerName;
                options.PollIntervalMs = intervalMs;
                options.ErrorDelayMs = Math.Clamp(intervalMs, 1000, 300000);
                options.DistributedLockName = NotificationDeliveryLatencyRollupBackgroundWorker.OwnershipLockName;
                options.OwnershipAcquireTimeoutMs = 100;
                options.OwnershipLeaseMs = Math.Clamp(intervalMs, 1000, 300000);
            });

        services.Configure<NotificationsSmsProviderOptions>(configuration.GetSection("Notifications:Sms"));
        services.AddHttpClient(TwilioSmsProviderAdapter.HttpClientName);
        services.AddHttpClient(MetaWhatsAppCloudProviderAdapter.HttpClientName);
        services.AddHttpClient(FcmPushProviderAdapter.HttpClientName);
        services.AddHttpClient(ApnsPushProviderAdapter.HttpClientName);
        services.AddHttpClient(WebPushProviderAdapter.HttpClientName);
        services.AddRateLimiter(rateLimiter =>
        {
            rateLimiter.AddFixedWindowLimiter(TwilioSmsWebhookController.RateLimiterPolicy, window =>
            {
                window.Window = TimeSpan.FromSeconds(1);
                window.PermitLimit = 20;
                window.QueueLimit = 0;
            });
            rateLimiter.AddFixedWindowLimiter(MetaWhatsAppWebhookController.RateLimiterPolicy, window =>
            {
                window.Window = TimeSpan.FromSeconds(1);
                window.PermitLimit = 20;
                window.QueueLimit = 0;
            });
            rateLimiter.AddFixedWindowLimiter(EmailFeedbackWebhookController.RateLimiterPolicy, window =>
            {
                window.Window = TimeSpan.FromSeconds(1);
                window.PermitLimit = 20;
                window.QueueLimit = 0;
            });
        });

        services.Configure<NotificationsEmailFeedbackOptions>(
            configuration.GetSection(NotificationsEmailFeedbackOptions.SectionName));

        services.Configure<NotificationsRateLimitOptions>(configuration.GetSection("Notifications:RateLimit"));

        services.Configure<NotificationsRetentionOptions>(configuration.GetSection("Notifications:Retention"));
        services.Configure<NotificationsDigestOptions>(configuration.GetSection("Notifications:Digests"));
        services.Configure<NotificationsProviderSecretRotationOptions>(configuration.GetSection("Notifications:ProviderSecrets:Rotation"));
        services.TryAddScoped<NotificationRetentionService>();
        var retentionWorkerOptions = NotificationsRetentionWorkerOptions.FromRetention(
            configuration.GetSection("Notifications:Retention").Get<NotificationsRetentionOptions>()
                ?? new NotificationsRetentionOptions());
        services.AddSharedPeriodicWorker<NotificationsRetentionWorker, NotificationsRetentionWorkerOptions>(
            options =>
            {
                options.Enabled = retentionWorkerOptions.Enabled;
                options.WorkerName = retentionWorkerOptions.WorkerName;
                options.PollIntervalMs = retentionWorkerOptions.PollIntervalMs;
                options.ErrorDelayMs = retentionWorkerOptions.ErrorDelayMs;
                options.DistributedLockName = retentionWorkerOptions.DistributedLockName;
                options.OwnershipAcquireTimeoutMs = retentionWorkerOptions.OwnershipAcquireTimeoutMs;
                options.OwnershipLeaseMs = retentionWorkerOptions.OwnershipLeaseMs;
            });

        var digestOptions = configuration.GetSection("Notifications:Digests").Get<NotificationsDigestOptions>()
            ?? new NotificationsDigestOptions();
        services.AddSharedPeriodicWorker<NotificationsDigestWorker, BackgroundWorkerOptions>(
            options =>
            {
                var intervalMs = checked((int)TimeSpan.FromSeconds(Math.Clamp(digestOptions.IntervalSeconds, 10, 3600)).TotalMilliseconds);
                options.Enabled = digestOptions.Enabled;
                options.WorkerName = NotificationsDigestWorker.WorkerName;
                options.PollIntervalMs = intervalMs;
                options.ErrorDelayMs = Math.Clamp(intervalMs, 1000, 60000);
                options.DistributedLockName = NotificationsDigestWorker.OwnershipLockName;
                options.OwnershipAcquireTimeoutMs = 100;
                options.OwnershipLeaseMs = Math.Clamp(intervalMs, 1000, 300000);
            });

        var rotationOptions = configuration.GetSection("Notifications:ProviderSecrets:Rotation")
            .Get<NotificationsProviderSecretRotationOptions>() ?? new NotificationsProviderSecretRotationOptions();
        services.AddSharedPeriodicWorker<NotificationsProviderSecretRotationWorker, BackgroundWorkerOptions>(
            options =>
            {
                var intervalMs = checked((int)TimeSpan.FromSeconds(Math.Clamp(rotationOptions.IntervalSeconds, 30, 86400)).TotalMilliseconds);
                options.Enabled = rotationOptions.Enabled;
                options.WorkerName = NotificationsProviderSecretRotationWorker.WorkerName;
                options.PollIntervalMs = intervalMs;
                options.ErrorDelayMs = Math.Clamp(intervalMs, 1000, 300000);
                options.DistributedLockName = NotificationsProviderSecretRotationWorker.OwnershipLockName;
                options.OwnershipAcquireTimeoutMs = 100;
                options.OwnershipLeaseMs = Math.Clamp(intervalMs, 1000, 300000);
            });

        var realtimeOptions = configuration.GetSection("Notifications:Realtime").Get<NotificationsRealtimeOptions>() ?? new NotificationsRealtimeOptions();
        if (realtimeOptions.Enabled)
        {
            var valkey = realtimeOptions.Valkey ?? new NotificationsRealtimeValkeyOptions();
            var valkeyConfiguration = ValkeyConnectionConfiguration.Create(configuration);
            if (!string.IsNullOrWhiteSpace(valkey.ChannelPrefix))
            {
                valkeyConfiguration.ChannelPrefix = RedisChannel.Literal(valkey.ChannelPrefix);
            }

            services.AddSignalR()
                .AddStackExchangeRedis(options => options.Configuration = valkeyConfiguration);

            services.Replace(ServiceDescriptor.Transient<INotificationsRealtimePublisher, SignalRNotificationsRealtimePublisher>());
            services.TryAddTransient<IdentitySessionRevokedEventHandler>();
        }

        services.Configure<SharedDataSeedOptions>(_ =>
        {
            _.Contributors.Add<SmsProviderConfigBootstrapSeedContributor>();
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

        var realtimeOptions = configuration
            .GetSection("Notifications:Realtime")
            .Get<NotificationsRealtimeOptions>() ?? new NotificationsRealtimeOptions();

        if (SharedAspNetCoreHostModule.IsHttpsRedirectionEnabled(configuration))
        {
            app.UseHttpsRedirection();
        }
        app.UseRouting();
        app.UseRateLimiter();
        app.UseUnitOfWork();
        app.UseAuditLogCapture();
        app.UseAuthentication();
        app.UseMiddleware<SessionValidationMiddleware>();
        app.UseMiddleware<MultiTenancyMiddleware>();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            if (realtimeOptions.Enabled)
            {
                endpoints.MapHub<NotificationsHub>("/hubs/notifications");
            }
        });
    }
}
