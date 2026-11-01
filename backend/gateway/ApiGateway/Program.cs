using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using ApiGateway;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenIddict.Validation.AspNetCore;
using Shared;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var configuration = builder.Configuration;
var environment = builder.Environment;

var gatewayLimits = configuration.GetSection(GatewayLimitsOptions.SectionName).Get<GatewayLimitsOptions>()
                    ?? new GatewayLimitsOptions();
GatewayLimitsOptions.EnsureValid(gatewayLimits);
builder.Services.AddOptions<GatewayLimitsOptions>()
    .Bind(configuration.GetSection(GatewayLimitsOptions.SectionName))
    .Validate(
        GatewayLimitsOptions.IsValid,
        $"{GatewayLimitsOptions.SectionName} must preserve the approved authorization/header budgets.")
    .ValidateOnStart();

var maxRequestBodyBytes = Math.Clamp(
    configuration.GetValue<long?>("GatewayLimits:MaxRequestBodyBytes") ?? 10 * 1024 * 1024,
    1024,
    100 * 1024 * 1024);
var requestTimeoutSeconds = Math.Clamp(
    configuration.GetValue<int?>("GatewayLimits:RequestTimeoutSeconds") ?? 30,
    1,
    300);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxRequestBodyBytes;
    options.Limits.MaxRequestHeadersTotalSize = gatewayLimits.MaxRequestHeadersTotalSize;
});

builder.Services.AddHealthChecks();
builder.Services.AddReverseProxy()
    .LoadFromConfig(configuration.GetSection("ReverseProxy"));
builder.Services.AddRequestTimeouts(options =>
{
    options.AddPolicy("gateway", new RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds),
        TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
        WriteTimeoutResponse = context => GatewayProblemDetails.WriteAsync(
            context,
            StatusCodes.Status504GatewayTimeout,
            SharedErrorCodes.RequestTimeout,
            "Request Timeout",
            "The gateway timed out before the request completed.")
    });
});

builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddOpenIddict()
    .AddValidation(options =>
    {
        var issuer = configuration["AuthServer:Authority"]
                     ?? (environment.IsDevelopment() ? "http://localhost:5234/" : null);
        if (string.IsNullOrWhiteSpace(issuer)
            || (!environment.IsDevelopment()
                && !issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Gateway AuthServer:Authority must be a non-empty HTTPS URL outside Development.");
        }

        var audiences = (configuration["AuthServer:Audiences"]
                         ?? "identity,tenant,permissions,auditlogs,notifications,auth,workflows")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        options.SetIssuer(new Uri(issuer));
        options.AddAudiences(audiences);
        options.UseSystemNetHttp();
        options.UseAspNetCore();
    });
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;
    options.ForwardLimit = 1;

    var knownProxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
    var knownNetworks = configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? Array.Empty<string>();

    if (environment.IsDevelopment())
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        return;
    }

    if (knownProxies.Length == 0 && knownNetworks.Length == 0)
    {
        return;
    }

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
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("Default", corsBuilder =>
    {
        var allowCredentials =
            string.Equals(configuration["Cors:AllowCredentials"], "true", StringComparison.OrdinalIgnoreCase);
        var allowWildcardSubdomains =
            string.Equals(configuration["Cors:AllowWildcardSubdomains"], "true", StringComparison.OrdinalIgnoreCase);

        var origins = (configuration["Cors:Origins"] ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();

        if (environment.IsDevelopment() && origins.Length == 0)
        {
            corsBuilder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            return;
        }

        if (origins.Length > 0)
        {
            corsBuilder.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
            if (allowWildcardSubdomains)
            {
                corsBuilder.SetIsOriginAllowedToAllowWildcardSubdomains();
            }
            if (allowCredentials)
            {
                corsBuilder.AllowCredentials();
            }
            return;
        }

        corsBuilder.SetIsOriginAllowed(_ => false);
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.HttpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        await GatewayProblemDetails.WriteAsync(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            SharedErrorCodes.RateLimitExceeded,
            "Too Many Requests",
            "The gateway rate limit has been exceeded. Retry after the indicated delay.",
            cancellationToken: cancellationToken);
    };
    options.AddPolicy("gateway", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"gateway:{ip}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = configuration.GetValue("RateLimiting:PermitLimit", 300),
                Window = TimeSpan.FromMinutes(configuration.GetValue("RateLimiting:WindowMinutes", 1)),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();

app.UseMiddleware<GatewayExceptionHandlingMiddleware>();

if (!environment.IsDevelopment())
{
    app.UseHsts();
}

if (IsHttpsRedirectionEnabled(configuration))
{
    app.UseHttpsRedirection();
}

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    GatewaySecurityPolicy.TryPromoteNotificationsHubAccessToken(context.Request);
    await next();
});
app.Use(async (context, next) =>
{
    if (!GatewaySecurityPolicy.HasAuthorizationHeaderWithinLimit(
            context.Request.Headers,
            gatewayLimits.MaxAuthorizationHeaderBytes))
    {
        await GatewayProblemDetails.WriteAsync(
            context,
            StatusCodes.Status431RequestHeaderFieldsTooLarge,
            SharedErrorCodes.RequestHeaderFieldsTooLarge,
            "Request Header Fields Too Large",
            "The Authorization header exceeds the gateway limit.");
        return;
    }

    await next();
});
app.UseRouting();
app.UseCors("Default");
app.UseRequestTimeouts();

app.Use(async (ctx, next) =>
{
    var csp =
        configuration["Security:Csp"]
        ?? "default-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; img-src 'self' data:; font-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self';";

    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Permissions-Policy"] = "accelerometer=(), ambient-light-sensor=(), autoplay=(), battery=(), camera=(), clipboard-read=(), clipboard-write=(), display-capture=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), publickey-credentials-create=(self), publickey-credentials-get=(self), usb=()";
    ctx.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
    ctx.Response.Headers["Content-Security-Policy"] = csp;

    await next();
});

app.UseRateLimiter();

app.Use(async (ctx, next) =>
{
    if (GatewaySecurityPolicy.IsBlockedPath(ctx.Request.Path))
    {
        // Deliberately return 404 to avoid disclosing internal surfaces.
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    GatewaySecurityPolicy.RemoveUntrustedHeaders(ctx.Request.Headers);
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Text("TenantWright ApiGateway"));
app.MapHealthChecks("/health");
app.MapHealthChecks("/alive", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});
app.MapReverseProxy(proxyPipeline =>
    {
        proxyPipeline.Use(async (context, next) =>
        {
            await next();
            await GatewayForwarderFailureMapper.TryWriteAsync(context);
        });
    })
    .RequireAuthorization()
    .RequireRateLimiting("gateway")
    .WithRequestTimeout("gateway");

app.Run();

static bool IsHttpsRedirectionEnabled(IConfiguration configuration)
{
    var value = configuration["Hosting:HttpsRedirection:Enabled"];
    return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
