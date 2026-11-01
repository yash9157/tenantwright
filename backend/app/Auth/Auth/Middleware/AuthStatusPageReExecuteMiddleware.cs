using Auth.Routing;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Auth.Middleware;

public sealed class AuthStatusPageReExecuteMiddleware
{
    private const string ReExecuteKey = "__AuthStatusPageReExecute";

    private readonly RequestDelegate _next;

    public AuthStatusPageReExecuteMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        if (context.Response.HasStarted || context.Items.ContainsKey(ReExecuteKey))
        {
            return;
        }

        if (!TryResolveStatusPage(context, out var statusPagePath))
        {
            return;
        }

        var originalPath = context.Request.Path;
        var originalQuery = context.Request.QueryString;
        var originalEndpoint = context.GetEndpoint();
        var routeValuesFeature = context.Features.Get<IRouteValuesFeature>();
        var originalRouteValues = routeValuesFeature?.RouteValues is null
            ? null
            : new RouteValueDictionary(routeValuesFeature.RouteValues);

        context.Items[ReExecuteKey] = true;
        context.Response.Clear();
        context.Request.Path = statusPagePath;
        context.Request.QueryString = QueryString.Empty;
        context.SetEndpoint(null);

        if (routeValuesFeature is not null)
        {
            routeValuesFeature.RouteValues = new RouteValueDictionary();
        }

        try
        {
            await _next(context);
        }
        finally
        {
            context.Request.Path = originalPath;
            context.Request.QueryString = originalQuery;
            context.SetEndpoint(originalEndpoint);

            if (routeValuesFeature is not null)
            {
                routeValuesFeature.RouteValues = originalRouteValues ?? new RouteValueDictionary();
            }

            context.Items.Remove(ReExecuteKey);
        }
    }

    private static bool TryResolveStatusPage(HttpContext context, out string statusPagePath)
    {
        statusPagePath = string.Empty;

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return false;
        }

        if (!IsBrowserDocumentRequest(context.Request))
        {
            return false;
        }

        var requestPath = context.Request.Path;
        if (requestPath.StartsWithSegments("/connect", StringComparison.OrdinalIgnoreCase)
            || requestPath.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase)
            || Path.HasExtension(requestPath.Value))
        {
            return false;
        }

        statusPagePath = context.Response.StatusCode switch
        {
            StatusCodes.Status404NotFound when !requestPath.StartsWithSegments(AuthPageRoutes.NotFound, StringComparison.OrdinalIgnoreCase)
                => AuthPageRoutes.NotFound,
            StatusCodes.Status503ServiceUnavailable when !requestPath.StartsWithSegments(AuthPageRoutes.Maintenance, StringComparison.OrdinalIgnoreCase)
                => AuthPageRoutes.Maintenance,
            _ => string.Empty
        };

        return statusPagePath.Length > 0;
    }

    private static bool IsBrowserDocumentRequest(HttpRequest request)
    {
        var secFetchDest = request.Headers["Sec-Fetch-Dest"].ToString();
        if (string.Equals(secFetchDest, "document", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var accept = request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(accept))
        {
            return true;
        }

        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }
}
