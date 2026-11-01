using System.Security.Claims;
using Auth.Claims;
using OpenIddict.Abstractions;
using Xunit;

namespace Auth.Tests;

public sealed class AuthenticationAssuranceClaimsTests
{
    [Fact]
    public async Task Assurance_claims_are_available_to_access_and_identity_tokens()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(OpenIddictConstants.Claims.AuthenticationMethodReference, "mfa"),
            new Claim(OpenIddictConstants.Claims.AuthenticationTime, "1730000000")
        }));

        await new OpenIddictClaimsPrincipalHandler().HandleAsync(principal);

        foreach (var claim in principal.Claims)
        {
            var destinations = claim.GetDestinations();
            Assert.Contains(OpenIddictConstants.Destinations.AccessToken, destinations);
            Assert.Contains(OpenIddictConstants.Destinations.IdentityToken, destinations);
        }
    }
}
