using System.Security.Claims;
using Hangfire.Dashboard;

namespace Shared.BackgroundTasks.Hangfire;

public sealed class InternalHangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly HashSet<string> _allowedRoles;
    private readonly HashSet<string> _allowedClientIds;

    public InternalHangfireDashboardAuthorizationFilter(
        IEnumerable<string> allowedRoles,
        IEnumerable<string> allowedClientIds)
    {
        _allowedRoles = allowedRoles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.Ordinal);
        _allowedClientIds = allowedClientIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.Ordinal);
    }

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return AuthorizePrincipal(httpContext.User);
    }

    internal bool AuthorizePrincipal(ClaimsPrincipal user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (_allowedRoles.Count > 0 && _allowedRoles.Any(user.IsInRole))
        {
            return true;
        }

        if (_allowedClientIds.Count == 0)
        {
            return false;
        }

        var clientId = user.FindFirstValue("client_id") ?? user.FindFirstValue("azp");
        return clientId != null && _allowedClientIds.Contains(clientId);
    }
}
