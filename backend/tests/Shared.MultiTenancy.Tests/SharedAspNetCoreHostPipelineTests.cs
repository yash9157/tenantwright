using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.AspNetCore;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Hosting;
using Shared;
using Shared.AspNetCore.Host;
using Shared.Modularity.Context;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class SharedAspNetCoreHostPipelineTests
{
    [Fact]
    public async Task Request_Log_Records_The_Mapped_Status_Of_A_Client_Error_Instead_Of_An_Unhandled_500()
    {
        var sink = new CapturingSink();
        using var requestLogger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        await using var services = BuildServices(requestLogger);
        var application = new ApplicationBuilder(services);
        new SharedAspNetCoreHostModule().OnApplicationInitialization(
            new ApplicationInitializationContext(services, application));
        application.Run(_ => Task.FromException(new SharedValidationException("The supplied value is invalid.")));

        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "test-trace" };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/test/resource";
        context.Response.Body = new MemoryStream();

        await application.Build()(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var completion = Assert.Single(sink.Events, IsRequestCompletion);
        Assert.Equal(LogEventLevel.Information, completion.Level);
        Assert.Null(completion.Exception);
        Assert.Equal("400", completion.Properties["StatusCode"].ToString());
    }

    private static ServiceProvider BuildServices(Serilog.ILogger requestLogger)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hosting:HttpsRedirection:Enabled"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton(new DiagnosticContext(requestLogger));
        services.AddSingleton<IDiagnosticContext>(provider => provider.GetRequiredService<DiagnosticContext>());
        services.Configure<RequestLoggingOptions>(options => options.Logger = requestLogger);
        services.AddCors(options => options.AddPolicy("Default", policy => policy.SetIsOriginAllowed(_ => false)));
        services.AddHealthChecks();
        return services.BuildServiceProvider();
    }

    private static bool IsRequestCompletion(LogEvent logEvent) =>
        logEvent.Properties.ContainsKey("StatusCode") && logEvent.Properties.ContainsKey("Elapsed");

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToList();
                }
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
            {
                _events.Add(logEvent);
            }
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Shared.MultiTenancy.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
