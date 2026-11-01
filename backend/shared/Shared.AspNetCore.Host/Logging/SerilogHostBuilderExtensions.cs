using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using System;

namespace Shared.AspNetCore.Host.Logging;

public static class SerilogHostBuilderExtensions
{
    public static IHostBuilder UseSharedSerilog(this IHostBuilder hostBuilder)
    {
        return hostBuilder.UseSerilog((context, services, loggerConfiguration) =>
        {
            loggerConfiguration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.With<ActivityTraceEnricher>()
                .Enrich.WithProperty("Application", context.HostingEnvironment.ApplicationName)
                .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
                .Enrich.WithProperty("service.name", context.HostingEnvironment.ApplicationName)
                .Enrich.WithProperty("service.version", typeof(SerilogHostBuilderExtensions).Assembly.GetName().Version?.ToString() ?? "unknown")
                .Enrich.WithProperty("deployment.environment", context.HostingEnvironment.EnvironmentName);

            var enableDevelopmentVerboseLogging =
                context.HostingEnvironment.IsDevelopment()
                && GetBoolean(context.Configuration["Serilog:DevelopmentVerbose"], defaultValue: false);

            if (enableDevelopmentVerboseLogging)
            {
                loggerConfiguration
                    .MinimumLevel.Debug()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Information)
                    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Information);
            }
            else if (!HasSerilogMinimumLevelConfigured(context.Configuration))
            {
                loggerConfiguration
                    .MinimumLevel.Is(LogEventLevel.Information)
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning);
            }

            if (!HasSerilogWriteToConfigured(context.Configuration))
            {
                loggerConfiguration
                    .WriteTo.Console(new JsonFormatter(renderMessage: true));
            }
        });
    }

    private static bool HasSerilogWriteToConfigured(IConfiguration configuration)
    {
        var section = configuration.GetSection("Serilog:WriteTo");
        return section.Exists() && section.GetChildren().Any();
    }

    private static bool HasSerilogMinimumLevelConfigured(IConfiguration configuration)
    {
        var section = configuration.GetSection("Serilog:MinimumLevel");
        return section.Exists() && section.GetChildren().Any();
    }

    private static bool GetBoolean(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }
}
