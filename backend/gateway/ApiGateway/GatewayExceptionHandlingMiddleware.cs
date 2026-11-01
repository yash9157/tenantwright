using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;

namespace ApiGateway;

public sealed class GatewayExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<GatewayExceptionHandlingMiddleware> _logger;

    public GatewayExceptionHandlingMiddleware(
        RequestDelegate next,
        IHostEnvironment environment,
        ILogger<GatewayExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _environment = environment;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.Response.HasStarted)
        {
            throw;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            GatewayProblemDetails.WriteClientAbortedResponse(context);
            _logger.LogDebug(
                "Gateway request was cancelled before a response was written. Path={RequestPath}",
                context.Request.Path.Value);
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogWarning(
                exception,
                "Gateway request timed out before a response was written. Path={RequestPath}",
                context.Request.Path.Value);
            await GatewayProblemDetails.WriteAsync(
                context,
                StatusCodes.Status504GatewayTimeout,
                SharedErrorCodes.RequestTimeout,
                "Request Timeout",
                "The gateway timed out before the request completed.");
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            _logger.LogError(
                exception,
                "Gateway request failed before a response was written. Path={RequestPath}",
                context.Request.Path.Value);
            await GatewayProblemDetails.WriteUnexpectedExceptionAsync(context, exception, _environment);
        }
    }
}
