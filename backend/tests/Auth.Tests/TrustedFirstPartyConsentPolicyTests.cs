using Auth.Authorization;
using OpenIddict.Abstractions;
using Xunit;

namespace Auth.Tests;

public sealed class TrustedFirstPartyConsentPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("implicit")]
    [InlineData("IMPLICIT")]
    public void Supported_values_normalize_to_implicit(string? value)
    {
        Assert.True(TrustedFirstPartyConsentPolicy.IsSupported(value));
        Assert.Equal(
            OpenIddictConstants.ConsentTypes.Implicit,
            TrustedFirstPartyConsentPolicy.Normalize(value));
    }

    [Theory]
    [InlineData("explicit")]
    [InlineData("systematic")]
    [InlineData("external")]
    public void Consent_modes_requiring_other_workflows_are_rejected(string value)
    {
        Assert.False(TrustedFirstPartyConsentPolicy.IsSupported(value));
        Assert.Throws<ArgumentException>(() => TrustedFirstPartyConsentPolicy.Normalize(value));
    }
}
