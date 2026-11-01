using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Auth.Authorization;
using Auth.Caching;
using Auth.Clients;
using Auth.Clients.Caching;
using Auth.EntityFrameworkCore;
using Auth.Middleware;
using Auth.Observability;
using Auth.Pages.Account.Validation;
using Auth.RateLimiting;
using Auth.Routing;
using Auth.TokenMetadata;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using Permissions.Application.Contracts.Definitions;
using Quartz;
using Shared.AspNetCore.Host;
using Shared.AspNetCore.Host.Configuration;
using Shared.AspNetCore.Host.Middleware;
using Shared.AuditLogs;
using Shared.AuditLogs.AspNetCore;
using Shared.Caching;
using Shared.Data;
using Shared.Data.Seeding;
using Shared.EntityFrameworkCore;
using Shared.Permissions;
using Shared.Permissions.Definitions.Auth;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.Security;
using Shared.Uow.Implementations;
using Shared.Uow.Options;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using StackExchange.Redis;
using System.Threading.RateLimiting;

namespace Auth;

[DependsOn(typeof(SharedAspNetCoreHostModule))]
[DependsOn(typeof(CachingModule))]
[DependsOn(typeof(SharedDataModule))]
[DependsOn(typeof(SharedEntityFrameworkCoreModule))]
[DependsOn(typeof(SharedAuditLogsModule))]
[DependsOn(typeof(SharedPermissionsModule))]
public class AuthHostModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.AddAuthHostServices();
        RegisterPermissionDefinitionProviders(context.Services);
    }

    internal static void RegisterPermissionDefinitionProviders(IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<PermissionDefinitionProvider, AuthPermissionDefinitionProvider>());
    }

    public override async Task OnApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        var app = context.ApplicationBuilder;
        var env = context.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = context.ServiceProvider.GetRequiredService<ILogger<AuthHostModule>>();
        var autoMigrate = string.Equals(
            configuration["EfCore:AutoMigrate"],
            "true",
            StringComparison.OrdinalIgnoreCase);
        var useQuartzPersistentStore = configuration.GetValue<bool?>("Auth:Quartz:UsePersistentStore") ?? false;
        var autoCreateQuartzSchema = configuration.GetValue<bool?>("Auth:Quartz:AutoCreateSchema") ?? true;
        QuartzSchemaInitializer.ValidateRuntimeConfiguration(
            configuration,
            useQuartzPersistentStore,
            autoCreateQuartzSchema);

        if (autoMigrate || useQuartzPersistentStore)
        {
            using var databaseScope = context.ServiceProvider.CreateScope();
            var authDbContext = databaseScope.ServiceProvider.GetRequiredService<AuthDbContext>();

            if (autoMigrate)
            {
                logger.LogInformation("Applying local EF Core migrations for {DbContext}.", typeof(AuthDbContext).FullName);
                await authDbContext.Database.MigrateAsync();
                logger.LogInformation("Completed local EF Core migrations for {DbContext}.", typeof(AuthDbContext).FullName);
            }

            if (useQuartzPersistentStore)
            {
                if (autoCreateQuartzSchema)
                {
                    await QuartzSchemaInitializer.EnsureCreatedAsync(authDbContext, logger);
                }
                else if (!await QuartzSchemaInitializer.IsCreatedAsync(authDbContext))
                {
                    throw StartupConfigExceptionFactory.InvalidConfiguration(
                        service: "tenantwright-auth",
                        key: "Auth:Quartz:UsePersistentStore|Auth:Quartz:AutoCreateSchema",
                        rule: "When persistent store is enabled and auto schema creation is disabled, QRTZ_* tables must already exist.",
                        fixHint: "Set 'Auth:Quartz:AutoCreateSchema=true' or manually create QRTZ_* tables before startup.");
                }
            }
        }

        using (var scope = context.ServiceProvider.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<IDataSeeder>();
            await seeder.SeedInSeparateUowAsync(
                options: new UnitOfWorkOptions { IsTransactional = true },
                requiresNew: true);

            // The OpenIddict caches are derived from the seeded clients and scopes, so they are warmed
            // only after seeding has committed. Running the warm-up as a seed contributor made its
            // order depend on contributor type names and, on a fresh database, cached every scope
            // with no resources, which issued audience-less tokens.
            await scope.ServiceProvider.GetRequiredService<IOpenIddictCacheSeedService>().SeedAsync();
        }

        if (SharedAspNetCoreHostModule.IsHttpsRedirectionEnabled(configuration))
        {
            app.UseHttpsRedirection();
        }
        app.UseStaticFiles();
        app.UseMiddleware<AuthStatusPageReExecuteMiddleware>();
        app.UseRouting();
        app.UseMiddleware<AuthLegacyRouteRedirectMiddleware>();
        app.UseUnitOfWork();
        app.UseMiddleware<TokenRequestTelemetryMiddleware>();
        app.UseAuditLogCapture();

        app.UseAuthentication();
        app.UseMiddleware<MultiTenancyMiddleware>();
        app.UseWhen(IsRevocationEndpoint, branch =>
        {
            branch.UseRateLimiter(CreateRevocationRateLimiterOptions());
        });
        app.UseRateLimiter();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            endpoints.MapRazorPages();
        });
    }

    internal static void ValidateInternalServiceUrl(string configurationKey, string? url, bool allowInsecureInternalHttp)
    {
        var envKey = configurationKey.Replace(":", "__", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw StartupConfigExceptionFactory.InvalidConfiguration(
                service: "tenantwright-auth",
                key: configurationKey,
                rule: "Must be a valid absolute URL outside Development.",
                fixHint: $"Set env '{envKey}' in charts/tenantwright-auth/values.yaml to a valid absolute URL and redeploy.");
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isAllowedInsecureInternalHttp =
            allowInsecureInternalHttp
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && IsInternalServiceHost(uri.Host);

        if (isAllowedInsecureInternalHttp)
        {
            return;
        }

        throw StartupConfigExceptionFactory.InvalidConfiguration(
            service: "tenantwright-auth",
            key: configurationKey,
            rule: "Must be an https URL outside Development, unless Services:AllowInsecureInternalHttp=true and host is internal (.svc/.svc.cluster.local or no dot).",
            fixHint: $"Set env '{envKey}' to https, or set 'Services__AllowInsecureInternalHttp=true' with an internal service host in charts/tenantwright-auth/values.yaml.");
    }

    private static bool IsInternalServiceHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".svc", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".svc.cluster.local", StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidatePublicBaseUrl(string? url)
    {
        var normalized = url?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized)
            && Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw StartupConfigExceptionFactory.InvalidConfiguration(
            service: "tenantwright-auth",
            key: "Auth:PublicBaseUrl",
            rule: "Must be a non-empty absolute https URL outside Development.",
            fixHint: "Set env 'Auth__PublicBaseUrl' in charts/tenantwright-auth/values.yaml to the public auth URL and redeploy.");
    }

    private static bool IsRevocationEndpoint(HttpContext context)
    {
        return context.Request.Path.Equals("/connect/revoke", StringComparison.OrdinalIgnoreCase);
    }

    private static RateLimiterOptions CreateRevocationRateLimiterOptions()
    {
        return new RateLimiterOptions
        {
            RejectionStatusCode = StatusCodes.Status429TooManyRequests,
            OnRejected = LogTokenRateLimitRejectionAsync,
            GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var rateLimitOptions = TokenRateLimiterKeyResolver.Normalize(
                    httpContext.RequestServices.GetRequiredService<IOptions<TokenRateLimitingOptions>>().Value);
                var requestInfo = TokenRateLimiterKeyResolver.ResolveForPartition(httpContext, rateLimitOptions);

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: requestInfo.PartitionKey,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rateLimitOptions.PermitLimit,
                        Window = TimeSpan.FromSeconds(rateLimitOptions.WindowSeconds),
                        QueueLimit = rateLimitOptions.QueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            })
        };
    }

    private static ValueTask LogTokenRateLimitRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var rateLimitOptions = TokenRateLimiterKeyResolver.Normalize(
            context.HttpContext.RequestServices.GetRequiredService<IOptions<TokenRateLimitingOptions>>().Value);
        var requestInfo = TokenRateLimiterKeyResolver.ResolveForRejection(context.HttpContext, rateLimitOptions);
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<AuthHostModule>>();

        logger.LogWarning(
            "Token rate limit rejected. ClientIdHash={ClientIdHash}, GrantType={GrantType}, PartitionMode={PartitionMode}, PartitionHash={PartitionHash}",
            TokenRateLimiterKeyResolver.HashClientId(requestInfo.ClientId),
            requestInfo.GrantType ?? "unknown",
            rateLimitOptions.PartitionMode,
            TokenRateLimiterKeyResolver.HashPartitionKey(requestInfo.PartitionKey));

        return ValueTask.CompletedTask;
    }
}
