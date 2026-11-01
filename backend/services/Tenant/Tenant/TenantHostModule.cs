using Tenant.Application;
using Tenant.Domain;
using Tenant.EntityFrameworkCore;
using Tenant.Workers;
using OpenTelemetry.Metrics;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Shared.AspNetCore.Host;
using Shared.AspNetCore.Host.Configuration;
using Shared.AspNetCore.Host.Middleware;
using Shared.AuditLogs;
using Shared.AuditLogs.AspNetCore;
using Shared.BackgroundTasks;
using Shared.BackgroundTasks.DependencyInjection;
using Shared.Data;
using Shared.Data.Seeding;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.Uow.Implementations;
using Shared.EventBus.Hosting;
using Shared.Uow.Abstractions;
using System;
using Shared.Permissions;
using System.Linq;
using Tenant.Application.Tenants;
using Shared.Permissions.Features;
using Permissions.Application.Contracts.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Data.ConnectionStrings;
using Shared.EventBus.Messaging;

namespace Tenant;

[DependsOn(typeof(TenantDomainModule))]
[DependsOn(typeof(TenantApplicationModule))]
[DependsOn(typeof(TenantEntityFrameworkCoreModule))]
[DependsOn(typeof(SharedAspNetCoreHostModule))]
[DependsOn(typeof(SharedAuditLogsModule))]
[DependsOn(typeof(SharedBackgroundTasksModule))]
[DependsOn(typeof(SharedDataModule))]
[DependsOn(typeof(SharedPermissionsModule))]
public class TenantHostModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;
        var configuration = context.Configuration;
        services.AddTenantWrightMessagingHost<global::Tenant.EntityFrameworkCore.EntityFrameworkCore.TenantDbContextApp>(
            configuration,
            TenantWrightMessagingTopology.TenantPrincipal);
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        // Configure Feature Checker (HTTP Client)
        services.Configure<FeatureCheckerClientOptions>(configuration.GetSection("RemoteFeatureChecker"));
        services.AddHttpClient(nameof(HttpFeatureCheckerClient));
        services.TryAddTransient<IFeatureCheckerClient, HttpFeatureCheckerClient>();
        services.AddOptions<TenantCacheOptions>()
            .Bind(configuration.GetSection("TenantCache"))
            .Validate(
                options => options.MaxAbsoluteExpirationHours is >= 1 and <= 8760,
                "TenantCache:MaxAbsoluteExpirationHours must be between 1 and 8760 hours.")
            .Validate(
                options => options.ReconcileIntervalSeconds is >= 10 and <= 86400,
                "TenantCache:ReconcileIntervalSeconds must be between 10 and 86400 seconds.")
            .ValidateOnStart();
        var tenantCacheOptions = configuration
            .GetSection("TenantCache")
            .Get<TenantCacheOptions>() ?? new TenantCacheOptions();
        var reconcileWorkerOptions = TenantCacheReconcileWorkerOptions.FromTenantCache(tenantCacheOptions);
        services.AddSharedPeriodicWorker<TenantCacheReconcileWorker, TenantCacheReconcileWorkerOptions>(
            options =>
            {
                options.Enabled = reconcileWorkerOptions.Enabled;
                options.WorkerName = reconcileWorkerOptions.WorkerName;
                options.PollIntervalMs = reconcileWorkerOptions.PollIntervalMs;
                options.ErrorDelayMs = reconcileWorkerOptions.ErrorDelayMs;
                options.DistributedLockName = reconcileWorkerOptions.DistributedLockName;
                options.OwnershipAcquireTimeoutMs = reconcileWorkerOptions.OwnershipAcquireTimeoutMs;
                options.OwnershipLeaseMs = reconcileWorkerOptions.OwnershipLeaseMs;
            });
        const string workflowReconciliationSection = "Tenant:Workers:OnboardingWorkflowReconciliation";
        services.AddOptions<TenantOnboardingWorkflowReconciliationOptions>()
            .Bind(configuration.GetSection(workflowReconciliationSection))
            .Validate(options => options.BatchSize is >= 1 and <= 100, "Onboarding workflow batch size must be 1–100.")
            .Validate(options => options.BacklogWarningThreshold is >= 1 and <= 1000,
                "Onboarding workflow backlog threshold must be 1–1000.")
            .ValidateOnStart();
        services.AddSharedPeriodicWorker<TenantOnboardingWorkflowReconciliationWorker,
            TenantOnboardingWorkflowReconciliationOptions>(configuration, workflowReconciliationSection, options =>
        {
            options.WorkerName = "tenant-onboarding-workflow-reconciliation";
            options.DistributedLockName = "tenant:onboarding-workflow-reconciliation";
            options.PollIntervalMs = 60_000;
            options.ErrorDelayMs = 60_000;
            options.OwnershipLeaseMs = 60_000;
        });
        services.AddHealthChecks().AddCheck<TenantOnboardingWorkflowHealthCheck>(
            "Tenant onboarding workflow recovery", tags: ["deep"], timeout: TimeSpan.FromSeconds(5));
        services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter("TenantWright.Tenant.WorkflowRecovery"));
        services.AddOptions<PublicTenantRegistrationCleanupOptions>()
            .Bind(configuration.GetSection("PublicTenantRegistrationCleanup"))
            .Validate(options => options.ExpiryHours is >= 24 and <= 720, "PublicTenantRegistrationCleanup:ExpiryHours must be between 24 and 720.")
            .Validate(options => options.BatchSize is >= 1 and <= 100, "PublicTenantRegistrationCleanup:BatchSize must be between 1 and 100.")
            .ValidateOnStart();
        var publicRegistrationCleanup = configuration.GetSection("PublicTenantRegistrationCleanup").Get<PublicTenantRegistrationCleanupOptions>() ?? new();
        services.AddSharedPeriodicWorker<PublicTenantRegistrationCleanupWorker, PublicTenantRegistrationCleanupOptions>(options =>
        {
            options.Enabled = publicRegistrationCleanup.Enabled;
            options.WorkerName = PublicTenantRegistrationCleanupWorker.WorkerName;
            options.PollIntervalMs = 3_600_000;
            options.ErrorDelayMs = 60_000;
            options.DistributedLockName = "tenant:public-registration:cleanup";
            options.OwnershipAcquireTimeoutMs = 100;
            options.OwnershipLeaseMs = 300_000;
            options.ExpiryHours = publicRegistrationCleanup.ExpiryHours;
            options.BatchSize = publicRegistrationCleanup.BatchSize;
        });
        services.AddOptions<TenantBootstrapSeedOptions>()
            .Bind(configuration.GetSection("BootstrapSeed"))
            .Validate(
                options =>
                    !options.Enabled ||
                    !options.DefaultTenant.Enabled ||
                    (options.DefaultTenant.Id != Guid.Empty &&
                        !string.IsNullOrWhiteSpace(options.DefaultTenant.Name)),
                "BootstrapSeed:DefaultTenant requires a non-empty Id and Name when bootstrap seeding is enabled.")
            .ValidateOnStart();

        if (!isDevelopment)
        {
            var authority = configuration["AuthServer:Authority"];
            if (string.IsNullOrWhiteSpace(authority) || !authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-tenant",
                    key: "AuthServer:Authority",
                    rule: "Must be a non-empty https URL outside Development.",
                    fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-tenant/values.yaml to an https URL and redeploy.");
            }

            var connectionString = configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-tenant",
                    key: "ConnectionStrings:Default",
                    rule: "Default connection string is required outside Development.",
                    fixHint: "Set secret ref 'ConnectionStrings__Default' to 'tenantwright-tenant-db.connection-string' in charts/tenantwright-tenant/values.yaml and ensure the secret exists.");
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
                        service: "tenantwright-tenant",
                        key: "AuthServer:Authority",
                        rule: "Authority is required to configure OpenIddict validation outside Development.",
                        fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-tenant/values.yaml and redeploy.");
                }
                options.SetIssuer(new Uri(issuer));
                options.AddAudiences("tenant");
                options.UseSystemNetHttp();
                options.UseAspNetCore();
            });

        services.AddAuthorization(options =>
        {
            var allowedInternalClients = (configuration["TenantInternal:AllowedClientIds"]
                ?? configuration["OpenIddict:Clients:Internal:ClientId"]
                ?? "auth-internal")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);
            var serviceabilityAllowedClients = (
                    configuration["TenantInternal:Serviceability:AllowedClientIds"]
                    ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);

            options.AddPolicy("TenantInternal", policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(ctx =>
                {
                    var scope = ctx.User.FindFirst(OpenIddictConstants.Claims.Scope)?.Value;
                    if (string.IsNullOrWhiteSpace(scope)) return false;
                    return scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tenant-internal", StringComparer.Ordinal);
                });
            });

            options.AddPolicy("TenantInternalProvisioning", policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(ctx =>
                {
                    var scope = ctx.User.FindFirst(OpenIddictConstants.Claims.Scope)?.Value;
                    if (string.IsNullOrWhiteSpace(scope))
                    {
                        return false;
                    }

                    var hasScope = scope
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Contains("tenant-internal", StringComparer.Ordinal);
                    if (!hasScope)
                    {
                        return false;
                    }

                    var clientId = ctx.User.FindFirst(OpenIddictConstants.Claims.ClientId)?.Value;
                    var subject = ctx.User.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
                    if (string.IsNullOrWhiteSpace(clientId) || !allowedInternalClients.Contains(clientId))
                    {
                        return false;
                    }

                    return string.Equals(subject, clientId, StringComparison.Ordinal);
                });
            });

            options.AddPolicy("TenantServiceabilityInternal", policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(ctx =>
                {
                    var scopes = ctx.User.FindFirst(OpenIddictConstants.Claims.Scope)?.Value
                        ?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    if (!scopes.Contains("tenant-serviceability.read", StringComparer.Ordinal)) return false;
                    var clientId = ctx.User.FindFirst(OpenIddictConstants.Claims.ClientId)?.Value;
                    var subject = ctx.User.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
                    return !string.IsNullOrWhiteSpace(clientId) &&
                           serviceabilityAllowedClients.Contains(clientId) &&
                           string.Equals(subject, clientId, StringComparison.Ordinal);
                });
            });
        });

        services.Configure<SharedDataSeedOptions>(_ =>
        {
            _.Contributors.Add<Tenant.Data.DefaultTenantDataSeedContributor>();
        });
    }

    public override async Task OnApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        var app = context.ApplicationBuilder;
        var env = context.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();

        using (var scope = context.ServiceProvider.CreateScope())
        {
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            using (var uow = uowManager.Begin())
            {
                var seeder = scope.ServiceProvider.GetRequiredService<IDataSeeder>();
                await seeder.SeedAsync();
                await uow.CompleteAsync();
            }
        }

        using (var scope = context.ServiceProvider.CreateScope())
        {
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            using (var uow = uowManager.Begin())
            {
                var connectionStringBackfill = scope.ServiceProvider
                    .GetRequiredService<TenantConnectionStringBackfillService>();
                await connectionStringBackfill.BackfillAsync();
                await uow.CompleteAsync();
            }
        }

        using (var scope = context.ServiceProvider.CreateScope())
        {
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            using (var uow = uowManager.Begin())
            {
                // Key rotation migration step: moves any connection string still
                // wrapped under a retired/old key onto the current key. See
                // TenantConnectionStringRewrapService for the two-deploy
                // retirement process this supports.
                var connectionStringRewrap = scope.ServiceProvider
                    .GetRequiredService<TenantConnectionStringRewrapService>();
                await connectionStringRewrap.RewrapAsync();
                await uow.CompleteAsync();
            }
        }

        using (var scope = context.ServiceProvider.CreateScope())
        {
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            using (var uow = uowManager.Begin())
            {
                // Stored target fingerprints are derived from the decrypted value; recompute them so duplicate-target
                // detection always uses the current normalization. See TenantConnectionTargetFingerprintRefreshService.
                var fingerprintRefresh = scope.ServiceProvider
                    .GetRequiredService<TenantConnectionTargetFingerprintRefreshService>();
                await fingerprintRefresh.RefreshAsync();
                await uow.CompleteAsync();
            }

            ReportMissingSharedDatabaseTargets(scope.ServiceProvider);
        }

        using (var scope = context.ServiceProvider.CreateScope())
        {
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            using (var uow = uowManager.Begin())
            {
                var tenantCacheManager = scope.ServiceProvider.GetRequiredService<TenantCacheManager>();
                await tenantCacheManager.WarmupAsync();
                await uow.CompleteAsync();
            }
        }

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

    /// <summary>
    /// Registration-time rejection of another service's shared database needs that service's target. Without it the
    /// owning service still fails the tenant closed at runtime, so a missing target is a warning, not a startup failure.
    /// </summary>
    private static void ReportMissingSharedDatabaseTargets(IServiceProvider services)
    {
        var configured = services.GetRequiredService<IOptions<TenantSharedDatabaseTargetsOptions>>().Value.SharedTargets;
        var missing = TenantWrightDatabaseCatalog.TenantDedicatable
            .Where(name => !configured.ContainsKey(name))
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        services.GetRequiredService<ILogger<TenantHostModule>>().LogWarning(
            "No shared database target is configured for {DatabaseNames} (TenantDatabases:SharedTargets); a dedicated assignment to those services' shared databases is only refused by the services themselves at runtime.",
            string.Join(", ", missing));
    }
}
