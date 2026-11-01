using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;
using Shared.AspNetCore.Host.Configuration;
using Shared.AspNetCore.Host.HealthChecks;
using Shared.AspNetCore.Host.Idempotency;
using Shared.AspNetCore.Host.Middleware;
using Shared.AspNetCore.ProblemDetails;
using Shared.Caching;
using Shared.Caching.Sessions;
using Shared.DistributedLocking;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;
using Shared.MultiTenancy;
using System;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Shared.AspNetCore.Host;

[DependsOn(typeof(MultiTenancyModule))]
[DependsOn(typeof(CachingModule))]
[DependsOn(typeof(DistributedLockingModule))]
public class SharedAspNetCoreHostModule : BaseModule
{
    private static readonly string[] ReadinessTags = ["ready"];
    private static readonly char[] CorsOriginSeparators = [',', ';'];

    public static bool IsHttpsRedirectionEnabled(IConfiguration configuration)
    {
        var value = configuration["Hosting:HttpsRedirection:Enabled"];
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Configuration;
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
        ProductionCredentialValidator.Validate(configuration, isDevelopment, "tenantwright-host");

        var healthChecksBuilder = context.Services.AddHealthChecks();

        var connectionString = configuration.GetConnectionString("Default");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            healthChecksBuilder.AddNpgSql(connectionString, name: "PostgreSQL", tags: ReadinessTags);
        }

        var valkeyConfiguration = configuration["Caching:Valkey:Configuration"];
        if (!string.IsNullOrWhiteSpace(valkeyConfiguration))
        {
            healthChecksBuilder.AddCheck<ValkeyReadinessHealthCheck>(
                name: "Valkey",
                tags: ReadinessTags);
        }

        context.Services.AddOptions<SessionValidationOptions>()
            .Bind(configuration.GetSection("SessionValidation"))
            .Validate(
                options => options.MarkerTtlHours is >= 1 and <= 8760,
                "SessionValidation:MarkerTtlHours must be between 1 and 8760 hours.")
            .Validate(
                options => options.IdleTimeoutMinutes is >= 1 and <= 1440,
                "SessionValidation:IdleTimeoutMinutes must be between 1 and 1440 minutes.")
            .Validate(
                options => options.MaxConcurrentSessions is >= 0 and <= 1000,
                "SessionValidation:MaxConcurrentSessions must be between 0 and 1000.")
            .Validate(
                options => options.CleanupIntervalSeconds is >= 10 and <= 86400,
                "SessionValidation:CleanupIntervalSeconds must be between 10 and 86400 seconds.")
            .Validate(
                options => options.RetentionDays is >= 1 and <= 3650,
                "SessionValidation:RetentionDays must be between 1 and 3650 days.")
            .ValidateOnStart();
        context.Services.Configure<IdempotencyOptions>(configuration.GetSection(IdempotencyOptions.SectionName));
        context.Services.TryAddSingleton<IIdempotencyStore, DistributedCacheIdempotencyStore>();
        context.Services.TryAddScoped<IdempotencyActionFilter>();
        context.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<MvcOptions>, IdempotencyFilterMvcOptionsSetup>());
        context.Services.AddAuthorization(SharedAuthorizationDefaults.Configure);
        context.Services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = actionContext =>
            {
                var problemDetails = SharedProblemDetailsFactory.CreateValidationProblemDetails(
                    actionContext.HttpContext,
                    actionContext.ModelState);
                var result = new BadRequestObjectResult(problemDetails);
                result.ContentTypes.Add(SharedProblemDetailsFactory.ContentType);
                return result;
            };
        });

        context.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedProto |
                ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;

            var knownProxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
            var knownNetworks = configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? Array.Empty<string>();

            if (isDevelopment)
            {
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            }
            else if (knownProxies.Length > 0 || knownNetworks.Length > 0)
            {
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();

                foreach (var proxy in knownProxies)
                {
                    if (IPAddress.TryParse(proxy, out var ip))
                    {
                        options.KnownProxies.Add(ip);
                    }
                }

                foreach (var network in knownNetworks)
                {
                    if (System.Net.IPNetwork.TryParse(network, out var ipNetwork))
                    {
                        options.KnownIPNetworks.Add(ipNetwork);
                    }
                }
            }
        });
        context.Services.AddCors(options =>
        {
            options.AddPolicy("Default", builder =>
            {
                var allowCredentials =
                    string.Equals(configuration["Cors:AllowCredentials"], "true", StringComparison.OrdinalIgnoreCase);
                var allowWildcardSubdomains =
                    string.Equals(configuration["Cors:AllowWildcardSubdomains"], "true", StringComparison.OrdinalIgnoreCase);

                var origins = (configuration["Cors:Origins"] ?? string.Empty)
                    .Split(CorsOriginSeparators, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToArray();

                if (isDevelopment && origins.Length == 0)
                {
                    builder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                    return;
                }

                if (origins.Length > 0)
                {
                    builder.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
                    if (allowWildcardSubdomains)
                    {
                        builder.SetIsOriginAllowedToAllowWildcardSubdomains();
                    }
                    if (allowCredentials)
                    {
                        builder.AllowCredentials();
                    }
                    return;
                }

                builder.SetIsOriginAllowed(_ => false);
            });
        });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.ApplicationBuilder;
        var env = context.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();

        app.UseForwardedHeaders();
        // Request logging wraps the exception handler so it records the status the client actually receives;
        // inside it, every mapped 4xx exception would be logged as an unhandled 500 error.
        app.UseSerilogRequestLogging();
        app.UseMiddleware<GlobalExceptionHandlingMiddleware>();

        if (!env.IsDevelopment())
        {
            app.UseHsts();
        }
        if (IsHttpsRedirectionEnabled(configuration))
        {
            app.UseHttpsRedirection();
        }
        app.UseCors("Default");

        app.Use(async (ctx, next) =>
        {
            var csp =
                configuration["Security:Csp"]
                ?? "default-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; img-src 'self' data:; font-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self';";

            // Scalar UI requires an inline module script to bootstrap. Keep this relaxation local-only.
            if (env.IsDevelopment() && ctx.Request.Path.StartsWithSegments("/scalar", StringComparison.Ordinal))
            {
                csp =
                    configuration["Security:ScalarCsp"]
                    ?? "default-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; img-src 'self' data:; font-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline';";
            }

            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["X-XSS-Protection"] = "1; mode=block";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["Permissions-Policy"] = "accelerometer=(), autoplay=(), camera=(), clipboard-read=(), clipboard-write=(), display-capture=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), publickey-credentials-create=(self), publickey-credentials-get=(self), usb=()";
            ctx.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
            ctx.Response.Headers["Content-Security-Policy"] = csp;
            await next();
        });

        app.UseHealthChecks(
            "/alive",
            SharedHealthCheckOptions.Create(check => check.Tags.Contains("live")));

        app.UseHealthChecks(
            "/health/live",
            SharedHealthCheckOptions.Create(check => check.Tags.Contains("live")));

        app.UseHealthChecks(
            "/health/ready",
            SharedHealthCheckOptions.Create(check => check.Tags.Contains("ready")));

        app.UseHealthChecks(
            "/health/deep",
            SharedHealthCheckOptions.Create(
                check => check.Tags.Contains("ready") || check.Tags.Contains("deep")));

        // Backward-compatible endpoint for existing tools.
        app.UseHealthChecks(
            "/health",
            SharedHealthCheckOptions.Create(check => check.Tags.Contains("ready")));
    }
}
