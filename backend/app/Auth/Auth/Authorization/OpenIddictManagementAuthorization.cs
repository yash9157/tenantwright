using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using OpenIddict.Abstractions;

namespace Auth.Authorization;

public static class OpenIddictManagementAuthorization
{
    public static bool IsTrustedInternalClient(
        ClaimsPrincipal user,
        ISet<string> allowedInternalClientIds)
    {
        var scopes = GetScopes(user);
        var clientId = user.FindFirst(OpenIddictConstants.Claims.ClientId)?.Value;
        var subject = user.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        return scopes.Contains("identity-internal")
               && allowedInternalClientIds.Contains(clientId)
               && string.Equals(subject, clientId, StringComparison.Ordinal);
    }

    public static bool IsTrustedInteractiveUser(
        ClaimsPrincipal user,
        ISet<string> allowedInteractiveClientIds)
    {
        var scopes = GetScopes(user);
        var clientId = user.FindFirst(OpenIddictConstants.Claims.ClientId)?.Value;
        var subject = user.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        if (!scopes.Contains("identity") ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(subject) ||
            !allowedInteractiveClientIds.Contains(clientId) ||
            string.Equals(subject, clientId, StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private static HashSet<string> GetScopes(ClaimsPrincipal user)
    {
        var scopeValues = user.FindAll(OpenIddictConstants.Claims.Scope)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return scopeValues.ToHashSet(StringComparer.Ordinal);
    }
}
