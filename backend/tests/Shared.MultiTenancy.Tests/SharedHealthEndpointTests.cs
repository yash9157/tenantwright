using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Shared.AspNetCore.Host.HealthChecks;
using Shared.AuditLogs.AspNetCore;
using Shared.Uow.Options;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public sealed class SharedHealthEndpointTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Endpoints_are_available_in_every_environment_with_minimal_responses(string environment)
    {
        await using var host = await StartHostAsync(environment);

        var alive = await host.Client.GetAsync("/alive");
        var ready = await host.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.Equal("{\"status\":\"Healthy\"}", await alive.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("{\"status\":\"Healthy\"}", await ready.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public async Task Readiness_returns_service_unavailable_for_non_healthy_status(HealthStatus status)
    {
        await using var host = await StartHostAsync(
            Environments.Production,
            services => services.AddHealthChecks().AddCheck(
                "dependency",
                () => new HealthCheckResult(status),
                tags: ["ready"]));

        var response = await host.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal($"{{\"status\":\"{status}\"}}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Liveness_ignores_unhealthy_checks_without_the_live_tag()
    {
        await using var host = await StartHostAsync(
            Environments.Production,
            services => services.AddHealthChecks().AddCheck(
                "dependency",
                () => HealthCheckResult.Unhealthy("must not be disclosed"),
                tags: ["ready"]));

        var alive = await host.Client.GetAsync("/alive");
        var ready = await host.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.Equal("{\"status\":\"Healthy\"}", await alive.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.DoesNotContain("dependency", await ready.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("must not be disclosed", await ready.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void Operational_health_paths_are_ignored_by_shared_auditing_and_unit_of_work()
    {
        Assert.Contains("/alive", new SharedAspNetCoreAuditingOptions().IgnoredUrls);
        Assert.Contains("/alive", new UnitOfWorkMiddlewareOptions().IgnoredUrls);
        Assert.Contains("/health", new SharedAspNetCoreAuditingOptions().IgnoredUrls);
        Assert.Contains("/health", new UnitOfWorkMiddlewareOptions().IgnoredUrls);
    }

    private static async Task<RunningHealthHost> StartHostAsync(
        string environment,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseHealthChecks(
            "/alive",
            SharedHealthCheckOptions.Create(check => check.Tags.Contains("live")));
        app.UseHealthChecks(
            "/health",
            SharedHealthCheckOptions.Create(check => check.Tags.Contains("ready")));
        await app.StartAsync();

        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses;
        var client = new HttpClient { BaseAddress = new Uri(addresses.Single()) };
        return new RunningHealthHost(app, client);
    }

    private sealed class RunningHealthHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
