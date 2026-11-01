using System.Security.Claims;
using Auth.Authorization;
using OpenIddict.Abstractions;
using Shared.Security;
using Xunit;

namespace Auth.Tests;

public sealed class LogoutRequestPolicyTests
{
    [Fact]
    public void Validated_matching_hint_should_allow_one_click_logout()
    {
        var userId = Guid.NewGuid().ToString("D");
        var sessionId = Guid.NewGuid().ToString("N");
        var currentUser = CreatePrincipal(userId, sessionId, "cookie");
        var hintPrincipal = CreatePrincipal(userId, sessionId, "hint");
        var request = CreateRequest();

        var result = LogoutRequestPolicy.CanCompleteWithoutConfirmation(
            currentUser,
            hintPrincipal,
            request);

        Assert.True(result);
    }

    [Theory]
    [InlineData("different-user", "session")]
    [InlineData("user", "different-session")]
    public void Mismatched_hint_should_require_confirmation(string subject, string sessionId)
    {
        var currentUser = CreatePrincipal("user", "session", "cookie");
        var hintPrincipal = CreatePrincipal(subject, sessionId, "hint");

        var result = LogoutRequestPolicy.CanCompleteWithoutConfirmation(
            currentUser,
            hintPrincipal,
            CreateRequest());

        Assert.False(result);
    }

    [Fact]
    public void Missing_redirect_proof_should_require_confirmation()
    {
        var currentUser = CreatePrincipal("user", "session", "cookie");
        var hintPrincipal = CreatePrincipal("user", "session", "hint");
        var request = CreateRequest();
        request.PostLogoutRedirectUri = null;

        var result = LogoutRequestPolicy.CanCompleteWithoutConfirmation(
            currentUser,
            hintPrincipal,
            request);

        Assert.False(result);
    }

    [Fact]
    public void Missing_cookie_with_valid_hint_should_allow_one_click_logout()
    {
        var currentUser = CreatePrincipal("user", "session", authenticationType: null);
        var hintPrincipal = CreatePrincipal("user", "session", "hint");

        var result = LogoutRequestPolicy.CanCompleteWithoutConfirmation(
            currentUser,
            hintPrincipal,
            CreateRequest());

        Assert.True(result);
    }

    [Fact]
    public void Hint_without_session_id_should_require_confirmation()
    {
        var currentUser = CreatePrincipal("user", "session", authenticationType: null);
        var hintPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(OpenIddictConstants.Claims.Subject, "user")],
            "hint"));

        var result = LogoutRequestPolicy.CanCompleteWithoutConfirmation(
            currentUser,
            hintPrincipal,
            CreateRequest());

        Assert.False(result);
    }

    [Fact]
    public void Completion_redirect_should_use_registered_client_callback()
    {
        var request = CreateRequest();

        var result = LogoutRequestPolicy.GetCompletionRedirectUri(request, "/auth/sign-in");

        Assert.Equal("https://app.example.test/auth/logout-callback", result);
    }

    [Fact]
    public void Completion_redirect_should_use_auth_fallback_without_client_callback()
    {
        var request = CreateRequest();
        request.PostLogoutRedirectUri = null;

        var result = LogoutRequestPolicy.GetCompletionRedirectUri(request, "/auth/sign-in");

        Assert.Equal("/auth/sign-in", result);
    }

    private static OpenIddictRequest CreateRequest()
    {
        return new OpenIddictRequest
        {
            IdTokenHint = "validated-id-token",
            PostLogoutRedirectUri = "https://app.example.test/auth/logout-callback"
        };
    }

    private static ClaimsPrincipal CreatePrincipal(
        string subject,
        string sessionId,
        string? authenticationType)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(OpenIddictConstants.Claims.Subject, subject),
                new Claim(SharedClaimTypes.SessionId, sessionId)
            ],
            authenticationType));
    }
}
