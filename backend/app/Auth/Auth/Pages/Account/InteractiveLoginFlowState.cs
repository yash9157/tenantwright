using Identity.Domain.Shared.Account;

namespace Auth.Pages.Account;

public sealed class InteractiveLoginFlowState
{
    public InteractiveLoginMode Mode { get; set; } = InteractiveLoginMode.Tenant;
    public Guid? TenantId { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public string LoginInput { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
    public bool RememberMe { get; set; }
    public string? DeviceFingerprint { get; set; }
    public Guid? ChallengeId { get; set; }
    public DateTimeOffset? ChallengeExpiresAtUtc { get; set; }
    public bool PasskeyAvailable { get; set; }
    public bool AuthenticatorCodeAvailable { get; set; }
    public bool RecoveryCodeAvailable { get; set; }
    public bool CodeBasedMfaAvailable => AuthenticatorCodeAvailable || RecoveryCodeAvailable;
    public bool PasskeyEnrollmentRequired { get; set; }
    public string AuthenticationMethod { get; set; } = "pwd";
    public DateTimeOffset? AuthenticatedAtUtc { get; set; }
}
