using Permissions.Application;
using Permissions.Domain;
using Permissions.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Caching;
using Permissions.Application.Grants;
using Permissions.Application.Seeding;
using Permissions.Authorization;
using Permissions.Workers;
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
using Shared.Permissions;
using Shared.Uow.Implementations;
using Shared.EventBus.Hosting;
using Shared.EventBus.Messaging;
using Shared.Uow.Options;
using System;
using System.Linq;
using Microsoft.Extensions.Options;
using Shared.Permissions.Features;

namespace Permissions;

[DependsOn(typeof(PermissionsDomainModule))]
[DependsOn(typeof(PermissionsApplicationModule))]
[DependsOn(typeof(PermissionsEntityFrameworkCoreModule))]
[DependsOn(typeof(SharedAspNetCoreHostModule))]
[DependsOn(typeof(SharedAuditLogsModule))]
[DependsOn(typeof(SharedBackgroundTasksModule))]
[DependsOn(typeof(SharedBackgroundTasksHangfireModule))]
[DependsOn(typeof(SharedDataModule))]
[DependsOn(typeof(SharedPermissionsModule))]
public class PermissionsHostModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;
        var configuration = context.Configuration;
        services.AddTenantWrightMessagingHost<global::Permissions.EntityFrameworkCore.EntityFrameworkCore.PermissionsDbContextApp>(
            configuration,
            TenantWrightMessagingTopology.PermissionsPrincipal);
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        services.AddHttpClient(nameof(InternalAccessTokenProvider));

        if (!isDevelopment)
        {
            var authority = configuration["AuthServer:Authority"];
            if (string.IsNullOrWhiteSpace(authority) || !authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-permissions",
                    key: "AuthServer:Authority",
                    rule: "Must be a non-empty https URL outside Development.",
                    fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-permissions/values.yaml to an https URL and redeploy.");
            }

            var connectionString = configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw StartupConfigExceptionFactory.InvalidConfiguration(
                    service: "tenantwright-permissions",
                    key: "ConnectionStrings:Default",
                    rule: "Default connection string is required outside Development.",
                    fixHint: "Set secret ref 'ConnectionStrings__Default' to 'tenantwright-permissions-db.connection-string' in charts/tenantwright-permissions/values.yaml and ensure the secret exists.");
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
                        service: "tenantwright-permissions",
                        key: "AuthServer:Authority",
                        rule: "Authority is required to configure OpenIddict validation outside Development.",
                        fixHint: "Set env 'AuthServer__Authority' in charts/tenantwright-permissions/values.yaml and redeploy.");
                }
                options.SetIssuer(new Uri(issuer));
                options.AddAudiences("permissions");
                options.UseSystemNetHttp();
                options.UseAspNetCore();
            });

        services.AddHttpContextAccessor();
        services.AddAuthorization(options =>
        {
            var allowedInternalClients = (configuration["PermissionsInternal:AllowedClientIds"]
                ?? configuration["OpenIddict:Clients:Internal:ClientId"]
                ?? "auth-internal")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);
            var subjectPermissionAuthorizationEnabled = configuration.GetValue<bool?>(
                "PermissionsInternal:SubjectPermissionAuthorization:Enabled") ?? false;
            var subjectPermissionAuthorizationAllowedClients = (
                    configuration["PermissionsInternal:SubjectPermissionAuthorization:AllowedClientIds"]
                    ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);
            var capabilityMetadataAllowedClients = (
                    configuration["PermissionsInternal:CapabilityMetadata:AllowedClientIds"]
                    ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);
            var entitlementProvisioningAllowedClients = (
                    configuration["PermissionsInternal:EntitlementProvisioning:AllowedClientIds"]
                    ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);

            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            options.AddPolicy(PermissionsHostPolicies.EvaluatePermissionsScope, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeAuthorizationRequirement(PermissionsScopes.EvaluatePermissions));
            });

            options.AddPolicy(PermissionsHostPolicies.SubjectPermissionAuthorizationInternal, policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeAuthorizationRequirement(
                    PermissionsScopes.SubjectPermissionAuthorizationsInternal));
                policy.RequireAssertion(ctx =>
                    subjectPermissionAuthorizationEnabled &&
                    IsAllowedInternalClient(ctx.User, subjectPermissionAuthorizationAllowedClients));
            });

            options.AddPolicy(PermissionsPolicies.CheckFeaturesInternal, policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeAuthorizationRequirement(PermissionsScopes.CheckFeaturesInternal));
                policy.RequireAssertion(ctx => IsAllowedInternalClient(ctx.User, allowedInternalClients));
            });

            options.AddPolicy(PermissionsPolicies.ReadCapabilityMetadataInternal, policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeAuthorizationRequirement(PermissionsScopes.ReadCapabilityMetadataInternal));
                policy.RequireAssertion(ctx => IsAllowedInternalClient(ctx.User, capabilityMetadataAllowedClients));
            });

            options.AddPolicy("PermissionsInternalProvisioning", policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeAuthorizationRequirement(PermissionsScopes.CheckFeaturesInternal));
                policy.RequireAssertion(ctx => IsAllowedInternalClient(ctx.User, allowedInternalClients));
            });

            options.AddPolicy(PermissionsHostPolicies.ProvisionTenantEntitlementsInternal, policy =>
            {
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ScopeAuthorizationRequirement(
                    PermissionsScopes.ProvisionTenantEntitlementsInternal));
                policy.RequireAssertion(ctx =>
                    IsAllowedInternalClient(ctx.User, entitlementProvisioningAllowedClients));
            });
        });

        services.AddSingleton<IAuthorizationHandler, ScopeAuthorizationHandler>();
        services.AddScoped<IRequestAuthorizationService, RequestAuthorizationService>();
        services.Configure<SharedDataSeedOptions>(options =>
        {
            options.Contributors.Add<DefinitionSyncSeedContributor>();
            options.Contributors.Add<DefaultManagementGrantsSeedContributor>();
            options.Contributors.Add<PermissionCacheSeedContributor>();
            options.Contributors.Add<FeatureCacheSeedContributor>();
        });

        services.Configure<PermissionGrantSweepOptions>(configuration.GetSection(PermissionGrantSweepOptions.SectionName));
        services.TryAddScoped<PermissionGrantSweepService>();
        var sweepOptions = configuration.GetSection(PermissionGrantSweepOptions.SectionName).Get<PermissionGrantSweepOptions>()
            ?? new PermissionGrantSweepOptions();
        var sweepWorkerOptions = PermissionGrantSweepWorkerOptions.FromSweepOptions(sweepOptions);
        services.AddSharedPeriodicWorker<PermissionGrantSweepWorker, PermissionGrantSweepWorkerOptions>(
            options =>
            {
                options.Enabled = sweepWorkerOptions.Enabled;
                options.WorkerName = sweepWorkerOptions.WorkerName;
                options.PollIntervalMs = sweepWorkerOptions.PollIntervalMs;
                options.ErrorDelayMs = sweepWorkerOptions.ErrorDelayMs;
                options.DistributedLockName = sweepWorkerOptions.DistributedLockName;
                options.OwnershipAcquireTimeoutMs = sweepWorkerOptions.OwnershipAcquireTimeoutMs;
                options.OwnershipLeaseMs = sweepWorkerOptions.OwnershipLeaseMs;
            });
    }

    private static bool IsAllowedInternalClient(
        System.Security.Claims.ClaimsPrincipal principal,
        HashSet<string> allowedInternalClients)
    {
        var clientId = principal.FindFirst(OpenIddictConstants.Claims.ClientId)?.Value;
        var subject = principal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        return !string.IsNullOrWhiteSpace(clientId)
            && allowedInternalClients.Contains(clientId)
            && string.Equals(subject, clientId, StringComparison.Ordinal);
    }

    public override async Task OnApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        var app = context.ApplicationBuilder;
        var env = context.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();

        using (var scope = context.ServiceProvider.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<IDataSeeder>();
            await seeder.SeedInSeparateUowAsync(
                options: new UnitOfWorkOptions { IsTransactional = true },
                requiresNew: true);
        }

        await ConfigurePermissionCacheRefreshRecurringJobAsync(context.ServiceProvider);

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

    private static async Task ConfigurePermissionCacheRefreshRecurringJobAsync(IServiceProvider serviceProvider)
    {
        var options = serviceProvider
            .GetRequiredService<IOptions<PermissionCacheRefreshOptions>>()
            .Value;
        const string recurringJobId = "permissions.cache-refresh";

        if (!options.Enabled)
        {
            return;
        }

        var recurringJobs = serviceProvider.GetRequiredService<IRecurringBackgroundJobManager>();

        await recurringJobs.AddOrUpdateAsync(
            recurringJobId,
            new PermissionCacheRefreshJobArgs(),
            options.RecurringCron,
            new RecurringBackgroundJobOptions
            {
                Queue = options.Queue,
                TimeZone = TimeZoneInfo.Utc
            });
    }

}
