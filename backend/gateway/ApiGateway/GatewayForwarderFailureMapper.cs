using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared;
using Yarp.ReverseProxy.Forwarder;

namespace ApiGateway;

public static class GatewayForwarderFailureMapper
{
    public static async Task TryWriteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        var failure = context.Features.Get<IForwarderErrorFeature>();
        if (failure is null || failure.Error == ForwarderError.None)
        {
            return;
        }

        var (statusCode, code, title, detail) = failure.Error switch
        {
            ForwarderError.NoAvailableDestinations => (
                StatusCodes.Status503ServiceUnavailable,
                SharedErrorCodes.UpstreamUnavailable,
                "Service Unavailable",
                "The requested service is temporarily unavailable."),
            ForwarderError.RequestTimedOut or ForwarderError.UpgradeActivityTimeout => (
                StatusCodes.Status504GatewayTimeout,
                SharedErrorCodes.UpstreamTimeout,
                "Gateway Timeout",
                "The requested service did not respond in time."),
            _ => (
                StatusCodes.Status502BadGateway,
                SharedErrorCodes.UpstreamFailure,
                "Bad Gateway",
                "The gateway could not reach the requested service.")
        };

        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(GatewayForwarderFailureMapper));
        logger.LogError(
            failure.Exception,
            "Gateway forwarding failed. Path={RequestPath} ForwarderError={ForwarderError} StatusCode={StatusCode} ErrorCode={ErrorCode}",
            context.Request.Path.Value,
            failure.Error,
            statusCode,
            code);

        await GatewayProblemDetails.WriteAsync(context, statusCode, code, title, detail);
    }
}
