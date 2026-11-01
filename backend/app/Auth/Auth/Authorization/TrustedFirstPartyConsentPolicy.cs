using OpenIddict.Abstractions;

namespace Auth.Authorization;

public static class TrustedFirstPartyConsentPolicy
{
    public static bool IsSupported(string? consentType) =>
        string.IsNullOrWhiteSpace(consentType) ||
        string.Equals(
            consentType.Trim(),
            OpenIddictConstants.ConsentTypes.Implicit,
            StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? consentType)
    {
        if (!IsSupported(consentType))
        {
            throw new ArgumentException(
                "Only implicit consent is supported for trusted first-party clients.",
                nameof(consentType));
        }

        return OpenIddictConstants.ConsentTypes.Implicit;
    }
}
