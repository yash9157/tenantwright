using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ApiGateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shared;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace Gateway.Tests;

public class GatewayProblemDetailsResponseTests
{
    [Fact]
    public async Task WriteAsync_returns_the_shared_rfc_7807_contract()
    {
        var context = CreateContext();

        await GatewayProblemDetails.WriteAsync(
            context,
            StatusCodes.Status429TooManyRequests,
            SharedErrorCodes.RateLimitExceeded,
            "Too Many Requests",
            "The gateway rate limit has been exceeded.");

        using var response = await ReadResponseAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal("urn:tenantwright:problem:rate_limit_exceeded", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("Too Many Requests", response.RootElement.GetProperty("title").GetString());
        Assert.Equal("rate_limit_exceeded", response.RootElement.GetProperty("code").GetString());
        Assert.Equal("test-trace", response.RootElement.GetProperty("traceId").GetString());
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task InvokeAsync_does_not_leak_unexpected_exception_details_in_production()
    {
        var context = CreateContext();
        var middleware = new GatewayExceptionHandlingMiddleware(
            _ => Task.FromException(new InvalidOperationException("internal upstream topology")),
            new TestHostEnvironment(Environments.Production),
            NullLogger<GatewayExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        using var response = await ReadResponseAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("internal_error", response.RootElement.GetProperty("code").GetString());
        Assert.Equal("An unexpected error occurred.", response.RootElement.GetProperty("detail").GetString());
        Assert.False(response.RootElement.TryGetProperty("details", out _));
        Assert.DoesNotContain("internal upstream topology", response.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryWriteAsync_maps_no_available_destinations_to_a_retryable_problem_response()
    {
        var context = CreateContext();
        context.Features.Set<IForwarderErrorFeature>(
            new TestForwarderErrorFeature(ForwarderError.NoAvailableDestinations));

        await GatewayForwarderFailureMapper.TryWriteAsync(context);

        using var response = await ReadResponseAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("upstream_unavailable", response.RootElement.GetProperty("code").GetString());
        Assert.Equal("Service Unavailable", response.RootElement.GetProperty("title").GetString());
    }

    private static DefaultHttpContext CreateContext()
    {
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "test-trace",
            RequestServices = services
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private sealed class TestForwarderErrorFeature(ForwarderError error) : IForwarderErrorFeature
    {
        public ForwarderError Error { get; } = error;

        public Exception? Exception => null;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Gateway.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
