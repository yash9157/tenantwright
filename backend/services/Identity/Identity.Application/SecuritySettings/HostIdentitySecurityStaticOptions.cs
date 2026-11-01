using Identity.Application.Contracts.SecuritySettings;

namespace Identity.Application.SecuritySettings;

public sealed class HostIdentitySecurityStaticOptions
{
    public HostIdentitySecuritySettingsDto DefaultSettings { get; init; } = new();

    public string LoginRiskHeaderCountryCodeName { get; init; } = "X-Country-Code";

    public bool LoginRiskGeoSignalTrusted { get; init; }

    public int PasswordResetTokenTtlMinutes { get; init; } = 15;

    public string PasswordResetTemplateName { get; init; } = "Identity.PasswordResetLink";

    public IReadOnlyList<string> PasswordResetAllowedBaseUrls { get; init; } = [];

    public string EmailVerificationTemplateName { get; init; } = "Identity.EmailVerificationLink";

    public IReadOnlyList<string> EmailVerificationAllowedBaseUrls { get; init; } = [];

    public string PublicInvitationTemplateName { get; init; } = "Identity.PublicInvitationLink";

    public string PublicInvitationSubject { get; init; } = "Your organization invited you to join";

    public IReadOnlyList<string> PublicInvitationAllowedBaseUrls { get; init; } = [];
}
