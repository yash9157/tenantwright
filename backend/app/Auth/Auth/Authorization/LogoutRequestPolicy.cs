using System.Security.Claims;
using OpenIddict.Abstractions;
using Shared.Security;

namespace Auth.Authorization;

public static class LogoutRequestPolicy
{
    public static bool CanCompleteWithoutConfirmation(
        ClaimsPrincipal currentUser,
        ClaimsPrincipal? identityTokenHintPrincipal,
        OpenIddictRequest request)
    {
        if (identityTokenHintPrincipal == null ||
            string.IsNullOrWhiteSpace(request.IdTokenHint) ||
            string.IsNullOrWhiteSpace(request.PostLogoutRedirectUri))
        {
            return false;
        }

        var hintedSubject = identityTokenHintPrincipal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        var hintedSessionId = identityTokenHintPrincipal.FindFirst(SharedClaimTypes.SessionId)?.Value;
        if (string.IsNullOrWhiteSpace(hintedSubject) || string.IsNullOrWhiteSpace(hintedSessionId))
        {
            return false;
        }

        if (currentUser.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        var currentSubject = currentUser.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        var currentSessionId = currentUser.FindFirst(SharedClaimTypes.SessionId)?.Value;

        return !string.IsNullOrWhiteSpace(currentSubject) &&
               !string.IsNullOrWhiteSpace(currentSessionId) &&
               string.Equals(currentSubject, hintedSubject, StringComparison.Ordinal) &&
               string.Equals(currentSessionId, hintedSessionId, StringComparison.Ordinal);
    }

    public static ClaimsPrincipal? GetSessionPrincipal(
        ClaimsPrincipal currentUser,
        ClaimsPrincipal? identityTokenHintPrincipal)
    {
        return currentUser.Identity?.IsAuthenticated == true
            ? currentUser
            : identityTokenHintPrincipal;
    }

    public static string GetCompletionRedirectUri(
        OpenIddictRequest request,
        string fallbackUri)
    {
        return string.IsNullOrWhiteSpace(request.PostLogoutRedirectUri)
            ? fallbackUri
            : request.PostLogoutRedirectUri;
    }
}
