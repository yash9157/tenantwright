using Microsoft.AspNetCore.Http;

namespace Shared.Uow.AspNetCore;

internal sealed class UnitOfWorkRequestState
{
    private static readonly object HttpContextItemKey = new();
    private int _failed;
    private int _middlewareSkipped;

    public bool HasFailed => Volatile.Read(ref _failed) != 0;

    public bool MiddlewareSkipped => Volatile.Read(ref _middlewareSkipped) != 0;

    public static UnitOfWorkRequestState GetOrCreate(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.Items.TryGetValue(HttpContextItemKey, out var value) &&
            value is UnitOfWorkRequestState existing)
        {
            return existing;
        }

        var created = new UnitOfWorkRequestState();
        httpContext.Items[HttpContextItemKey] = created;
        return created;
    }

    public static bool HasRequestFailed(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.Items.TryGetValue(HttpContextItemKey, out var value) &&
               value is UnitOfWorkRequestState state &&
               state.HasFailed;
    }

    public static bool WasMiddlewareSkipped(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.Items.TryGetValue(HttpContextItemKey, out var value) &&
               value is UnitOfWorkRequestState state &&
               state.MiddlewareSkipped;
    }

    public void MarkFailed()
    {
        Volatile.Write(ref _failed, 1);
    }

    public void MarkMiddlewareSkipped()
    {
        Volatile.Write(ref _middlewareSkipped, 1);
    }
}
