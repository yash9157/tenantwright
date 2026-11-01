using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Permissions.Authorization;

public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeAuthorizationRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeAuthorizationRequirement requirement)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        if (requirement.RequiredScopes.Count == 0)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var scopes = new HashSet<string>(StringComparer.Ordinal);
        AddScopes(scopes, context.User, "scope");
        AddScopes(scopes, context.User, "scp");

        foreach (var required in requirement.RequiredScopes)
        {
            if (scopes.Contains(required))
            {
                context.Succeed(requirement);
                break;
            }
        }

        return Task.CompletedTask;
    }

    private static void AddScopes(HashSet<string> scopes, ClaimsPrincipal principal, string claimType)
    {
        foreach (var claim in principal.FindAll(claimType))
        {
            var value = claim.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                scopes.Add(token);
            }
        }
    }
}
