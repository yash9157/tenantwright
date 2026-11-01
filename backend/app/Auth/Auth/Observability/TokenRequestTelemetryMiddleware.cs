using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Auth.Observability;

public sealed class TokenRequestTelemetryMiddleware
{
    public const string TokenDbQueryCountItemKey = "Auth:Token:DbQueryCount";

    private readonly RequestDelegate _next;
    private readonly ILogger<TokenRequestTelemetryMiddleware> _logger;

    public TokenRequestTelemetryMiddleware(
        RequestDelegate next,
        ILogger<TokenRequestTelemetryMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsTokenEndpoint(context.Request.Path))
        {
            await _next(context);
            return;
        }

        context.Items[TokenDbQueryCountItemKey] = 0;
        var sw = Stopwatch.StartNew();

        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();

            var queryCount = 0;
            if (context.Items.TryGetValue(TokenDbQueryCountItemKey, out var rawCount) && rawCount is int intCount)
            {
                queryCount = intCount;
            }

            _logger.LogInformation(
                "Token request metrics. StatusCode={StatusCode}, DurationMs={DurationMs}, DbQueryCount={DbQueryCount}",
                context.Response.StatusCode,
                sw.ElapsedMilliseconds,
                queryCount);
        }
    }

    public static void IncrementDbQueryCount(HttpContext? httpContext)
    {
        if (httpContext == null || !IsTokenEndpoint(httpContext.Request.Path))
        {
            return;
        }

        if (httpContext.Items.TryGetValue(TokenDbQueryCountItemKey, out var rawCount) && rawCount is int count)
        {
            httpContext.Items[TokenDbQueryCountItemKey] = count + 1;
            return;
        }

        httpContext.Items[TokenDbQueryCountItemKey] = 1;
    }

    private static bool IsTokenEndpoint(PathString path)
        => path.Equals("/connect/token", StringComparison.OrdinalIgnoreCase);
}
