using Shared.Security;
using Xunit;

namespace Shared.Security.Tests;

public sealed class ClaimValueParserTests
{
    [Fact]
    public void TryParseCanonicalGuidClaims_sorts_and_deduplicates_valid_values()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var valid = ClaimValueParser.TryParseCanonicalGuidClaims(
            [second.ToString("D"), first.ToString("D"), first.ToString("D")],
            32,
            out var identifiers);

        Assert.True(valid);
        Assert.Equal([first, second], identifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111111111111111111111111111")]
    public void TryParseCanonicalGuidClaims_rejects_invalid_or_noncanonical_values(string value)
    {
        Assert.False(ClaimValueParser.TryParseCanonicalGuidClaims([value], 32, out var identifiers));
        Assert.Empty(identifiers);
    }

    [Fact]
    public void TryParseCanonicalGuidClaims_rejects_values_above_the_configured_limit()
    {
        var values = Enumerable.Range(1, 33).Select(_ => Guid.NewGuid().ToString("D"));

        Assert.False(ClaimValueParser.TryParseCanonicalGuidClaims(values, 32, out var identifiers));
        Assert.Empty(identifiers);
    }
}
