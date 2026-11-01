using System.Security.Claims;
using Auth.Claims;
using OpenIddict.Abstractions;
using Shared.Security;
using Xunit;

namespace Auth.Tests;

public sealed class OpenIddictClaimsPrincipalHandlerTests
{
    [Fact]
    public async Task HandleAsync_should_keep_profile_pii_out_of_access_token_destinations()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, "user-1"));
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Name, "jane"));
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Email, "jane@example.com"));
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.PhoneNumber, "+15550001000"));
        identity.SetScopes(
            OpenIddictConstants.Scopes.OpenId,
            OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Scopes.Phone);
        var principal = new ClaimsPrincipal(identity);

        await new OpenIddictClaimsPrincipalHandler().HandleAsync(principal);

        Assert.Contains(OpenIddictConstants.Destinations.AccessToken, FindClaim(principal, OpenIddictConstants.Claims.Subject).GetDestinations());
        Assert.DoesNotContain(OpenIddictConstants.Destinations.AccessToken, FindClaim(principal, OpenIddictConstants.Claims.Name).GetDestinations());
        Assert.DoesNotContain(OpenIddictConstants.Destinations.AccessToken, FindClaim(principal, OpenIddictConstants.Claims.Email).GetDestinations());
        Assert.DoesNotContain(OpenIddictConstants.Destinations.AccessToken, FindClaim(principal, OpenIddictConstants.Claims.PhoneNumber).GetDestinations());
        Assert.Contains(OpenIddictConstants.Destinations.IdentityToken, FindClaim(principal, OpenIddictConstants.Claims.Email).GetDestinations());
    }

    [Fact]
    public async Task HandleAsync_should_send_role_to_both_access_and_identity_tokens_when_roles_scope_is_granted()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, "user-1"));
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Role, "admin"));
        identity.SetScopes(OpenIddictConstants.Scopes.OpenId, OpenIddictConstants.Scopes.Roles);
        var principal = new ClaimsPrincipal(identity);

        await new OpenIddictClaimsPrincipalHandler().HandleAsync(principal);

        var destinations = FindClaim(principal, OpenIddictConstants.Claims.Role).GetDestinations();
        Assert.Contains(OpenIddictConstants.Destinations.AccessToken, destinations);
        Assert.Contains(OpenIddictConstants.Destinations.IdentityToken, destinations);
    }

    [Fact]
    public async Task HandleAsync_should_not_send_role_to_any_token_when_roles_scope_is_not_granted()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, "user-1"));
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Role, "admin"));
        identity.SetScopes(OpenIddictConstants.Scopes.OpenId);
        var principal = new ClaimsPrincipal(identity);

        await new OpenIddictClaimsPrincipalHandler().HandleAsync(principal);

        var destinations = FindClaim(principal, OpenIddictConstants.Claims.Role).GetDestinations();
        Assert.DoesNotContain(OpenIddictConstants.Destinations.AccessToken, destinations);
        Assert.DoesNotContain(OpenIddictConstants.Destinations.IdentityToken, destinations);
    }

    [Fact]
    public async Task HandleAsync_should_send_immutable_role_id_to_access_token_only()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, "user-1"));
        identity.AddClaim(new Claim(SharedClaimTypes.IdentityRoleId, Guid.NewGuid().ToString("D")));
        identity.SetScopes(OpenIddictConstants.Scopes.OpenId, OpenIddictConstants.Scopes.Roles);
        var principal = new ClaimsPrincipal(identity);

        await new OpenIddictClaimsPrincipalHandler().HandleAsync(principal);

        var destinations = FindClaim(principal, SharedClaimTypes.IdentityRoleId).GetDestinations();
        Assert.Contains(OpenIddictConstants.Destinations.AccessToken, destinations);
        Assert.DoesNotContain(OpenIddictConstants.Destinations.IdentityToken, destinations);
    }

    [Fact]
    public async Task HandleAsync_should_send_session_id_to_access_and_identity_tokens()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, "user-1"));
        identity.AddClaim(new Claim(SharedClaimTypes.SessionId, Guid.NewGuid().ToString("N")));
        identity.SetScopes(OpenIddictConstants.Scopes.OpenId);
        var principal = new ClaimsPrincipal(identity);

        await new OpenIddictClaimsPrincipalHandler().HandleAsync(principal);

        var destinations = FindClaim(principal, SharedClaimTypes.SessionId).GetDestinations();
        Assert.Contains(OpenIddictConstants.Destinations.AccessToken, destinations);
        Assert.Contains(OpenIddictConstants.Destinations.IdentityToken, destinations);
    }

    private static Claim FindClaim(ClaimsPrincipal principal, string claimType)
    {
        return principal.Claims.Single(claim => claim.Type == claimType);
    }
}
