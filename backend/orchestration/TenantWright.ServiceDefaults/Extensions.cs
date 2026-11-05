using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.Configure(options =>
        {
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId |
                ActivityTrackingOptions.SpanId |
                ActivityTrackingOptions.ParentId;
        });

        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: builder.Environment.ApplicationName,
                serviceVersion: System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
                serviceInstanceId: Environment.MachineName))
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Identity.LoginRisk")
                    .AddMeter("AuditLogs.Ingestion")
                    .AddMeter("AuditLogs.Archive")
                    .AddMeter("AuditLogs.OperationalHealth")
                    .AddMeter("Notifications.Dispatch")
                    .AddMeter("Notifications.Delivery")
                    .AddMeter("Notifications.RateLimit")
                    .AddMeter("Shared.Data.ConnectionStrings")
                    .AddMeter("Shared.BackgroundTasks")
                    .AddMeter("Shared.EventBus.Outbox")
                    .AddMeter("Shared.EventBus.Inbox")
                    .AddMeter("Shared.EventBus.Sender")
                    // Wolverine names its meter "Wolverine:{Messaging:ServiceName}" when it is the messaging provider.
                    .AddMeter("Wolverine:*");
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource("Notifications.Delivery")
                    .AddSource("Wolverine")
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.Ordinal)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath, StringComparison.Ordinal)
                    )
                    .AddEntityFrameworkCoreInstrumentation()
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation(options => options.FilterHttpRequestMessage = ShouldInstrumentHttpRequest)
                    .AddRedisInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static bool ShouldInstrumentHttpRequest(HttpRequestMessage request)
    {
        // Classification belongs to the caller. This generic scope flag keeps sensitive URLs out of HTTP spans.
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
            if (activity.GetTagItem("telemetry.suppress_http_instrumentation") is true)
                return false;
        return true;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter =
            !IsTelemetryDisabled(builder.Configuration["OTEL_SDK_DISABLED"]) &&
            !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    private static bool IsTelemetryDisabled(string? value) =>
        bool.TryParse(value, out var disabled) && disabled;

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

}
