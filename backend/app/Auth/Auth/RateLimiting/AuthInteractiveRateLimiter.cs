using Microsoft.AspNetCore.Http;

namespace Auth.RateLimiting;

public static class AuthInteractiveRateLimiter
{
    public static bool ShouldLimit(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return HttpMethods.IsPost(httpContext.Request.Method);
    }
}
