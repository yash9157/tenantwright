using Microsoft.AspNetCore.Builder;
using Shared.AuditLogs.Middleware;

namespace Shared.AuditLogs.AspNetCore;

public static class AuditLogApplicationBuilderExtensions
{
    public static IApplicationBuilder UseAuditLogCapture(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<AuditLogCaptureMiddleware>();
    }
}
