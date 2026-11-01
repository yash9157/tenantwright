using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shared;
using Shared.AspNetCore.Host.Middleware;
using Shared.AspNetCore.ProblemDetails;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class GlobalExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_Returns_Validation_ProblemDetails_With_Safe_Message_And_Field_Errors_In_Production()
    {
        var exception = new SharedValidationException(
            "The supplied date is invalid.",
            new Dictionary<string, string[]>
            {
                ["startDate"] = ["The start date must be in the future."]
            });
        var context = await InvokeAsync(exception, Environments.Production);

        using var response = await ReadResponseAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.StartsWith(SharedProblemDetailsFactory.ContentType, context.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal("urn:tenantwright:problem:validation_failed", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("Bad Request", response.RootElement.GetProperty("title").GetString());
        Assert.Equal("The supplied date is invalid.", response.RootElement.GetProperty("detail").GetString());
        Assert.Equal("validation_failed", response.RootElement.GetProperty("code").GetString());
        Assert.Equal("test-trace", response.RootElement.GetProperty("traceId").GetString());
        Assert.Equal(
            "The start date must be in the future.",
            response.RootElement.GetProperty("errors").GetProperty("startDate")[0].GetString());
    }

    [Theory]
    [InlineData(typeof(SharedUnauthorizedException), StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized")]
    [InlineData(typeof(SharedForbiddenException), StatusCodes.Status403Forbidden, "forbidden", "Forbidden")]
    [InlineData(typeof(SharedConflictException), StatusCodes.Status409Conflict, "conflict", "Conflict")]
    public async Task InvokeAsync_Returns_Expected_ProblemDetails_For_Shared_Client_Errors(
        Type exceptionType,
        int expectedStatus,
        string expectedCode,
        string expectedTitle)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;
        var context = await InvokeAsync(exception, Environments.Production);

        using var response = await ReadResponseAsync(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal($"urn:tenantwright:problem:{expectedCode}", response.RootElement.GetProperty("type").GetString());
        Assert.Equal(expectedTitle, response.RootElement.GetProperty("title").GetString());
        Assert.Equal(expectedCode, response.RootElement.GetProperty("code").GetString());
        Assert.NotEmpty(response.RootElement.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task InvokeAsync_Does_Not_Leak_Unexpected_Exception_Details_In_Production()
    {
        var context = await InvokeAsync(new InvalidOperationException("database host name should remain private"), Environments.Production);

        using var response = await ReadResponseAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("internal_error", response.RootElement.GetProperty("code").GetString());
        Assert.Equal("An unexpected error occurred.", response.RootElement.GetProperty("detail").GetString());
        Assert.False(response.RootElement.TryGetProperty("details", out _));
        Assert.DoesNotContain("database host name", response.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Does_Not_Expose_A_Base_SharedException_Message_In_Production()
    {
        var context = await InvokeAsync(
            new SharedException("cache key and topology should remain private"),
            Environments.Production);

        using var response = await ReadResponseAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("internal_error", response.RootElement.GetProperty("code").GetString());
        Assert.Equal("An unexpected error occurred.", response.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("cache key", response.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Composed_Development_Pipeline_Uses_ProblemDetails_Instead_Of_Html()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development))
            .BuildServiceProvider();
        var application = new ApplicationBuilder(services);
        application.UseMiddleware<GlobalExceptionHandlingMiddleware>();
        application.Run(_ => Task.FromException(new InvalidOperationException("development diagnostic")));

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            TraceIdentifier = "test-trace"
        };
        context.Response.Body = new MemoryStream();

        await application.Build()(context);
        using var response = await ReadResponseAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.StartsWith(SharedProblemDetailsFactory.ContentType, context.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal("development diagnostic", response.RootElement.GetProperty("detail").GetString());
        Assert.Contains("InvalidOperationException", response.RootElement.GetProperty("details").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Does_Not_Attach_Exception_Graphs_To_Expected_Client_Error_Logs()
    {
        var logger = new RecordingLogger<GlobalExceptionHandlingMiddleware>();
        await InvokeAsync(
            new SharedConflictException(
                "The request conflicts with existing data.",
                new InvalidOperationException("provider detail with a sensitive value")),
            Environments.Production,
            logger: logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task InvokeAsync_Treats_ArgumentNullException_As_A_Server_Error()
    {
        var context = await InvokeAsync(new ArgumentNullException("tenantId"), Environments.Production);

        using var response = await ReadResponseAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("internal_error", response.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvokeAsync_Returns_No_Body_For_A_Client_Aborted_Request()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var context = await InvokeAsync(
            new OperationCanceledException(cancellationTokenSource.Token),
            Environments.Production,
            requestAborted: cancellationTokenSource.Token);

        Assert.Equal(499, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.True(string.IsNullOrEmpty(context.Response.ContentType));
    }

    [Fact]
    public async Task InvokeAsync_Returns_A_Timeout_ProblemDetails_For_A_Non_Aborted_Cancellation()
    {
        var context = await InvokeAsync(new OperationCanceledException(), Environments.Production);

        using var response = await ReadResponseAsync(context);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
        Assert.Equal("request_timeout", response.RootElement.GetProperty("code").GetString());
        Assert.Equal("Request Timeout", response.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("/connect/token")]
    [InlineData("/connect/userinfo")]
    [InlineData("/.well-known/openid-configuration")]
    public async Task InvokeAsync_Rethrows_Exceptions_For_OpenIddict_Protocol_Paths(string path)
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "test-trace"
        };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlingMiddleware(
            _ => Task.FromException(new InvalidOperationException("protocol handler owns the response")),
            NullLogger<GlobalExceptionHandlingMiddleware>.Instance,
            new TestHostEnvironment(Environments.Production));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.True(string.IsNullOrEmpty(context.Response.ContentType));
    }

    private static async Task<DefaultHttpContext> InvokeAsync(
        Exception exception,
        string environmentName,
        ILogger<GlobalExceptionHandlingMiddleware>? logger = null,
        CancellationToken requestAborted = default)
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "test-trace"
        };
        context.RequestAborted = requestAborted;
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlingMiddleware(
            _ => Task.FromException(exception),
            logger ?? NullLogger<GlobalExceptionHandlingMiddleware>.Instance,
            new TestHostEnvironment(environmentName));

        await middleware.InvokeAsync(context);
        return context;
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Shared.MultiTenancy.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);
}
