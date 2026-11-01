using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Shared;
using Shared.AspNetCore.ProblemDetails;

namespace ApiGateway;

public static class GatewayProblemDetails
{
    private const int ClientClosedRequestStatusCode = 499;
    private const string UnexpectedErrorMessage = "An unexpected error occurred.";

    public static Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string title,
        string detail,
        string? developmentDetails = null,
        CancellationToken cancellationToken = default)
    {
        var problemDetails = SharedProblemDetailsFactory.CreateProblemDetails(
            context,
            statusCode,
            code,
            title,
            detail,
            developmentDetails);

        return SharedProblemDetailsWriter.WriteAsync(context, problemDetails, cancellationToken);
    }

    public static Task WriteUnexpectedExceptionAsync(
        HttpContext context,
        Exception exception,
        IHostEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(environment);

        var isDevelopment = environment.IsDevelopment();
        return WriteAsync(
            context,
            StatusCodes.Status500InternalServerError,
            SharedErrorCodes.InternalError,
            "Internal Server Error",
            isDevelopment ? exception.Message : UnexpectedErrorMessage,
            isDevelopment ? exception.ToString() : null,
            cancellationToken);
    }

    public static void WriteClientAbortedResponse(HttpContext context)
    {
        context.Response.StatusCode = ClientClosedRequestStatusCode;
        context.Response.Headers.CacheControl = "no-store";
    }
}
