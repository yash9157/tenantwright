using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Identity.Domain.Shared.Account;
using Identity.Application.Contracts.Passkeys;

namespace Identity.Application.Contracts.Account;

public interface IAccountAppService
{
    Task<LoginResultDto> CheckPasswordAsync(
        string loginInput,
        string password,
        Guid? tenantId = null,
        InteractiveLoginMode mode = InteractiveLoginMode.Tenant,
        string? sessionId = null,
        string? clientId = null,
        string? clientIpAddress = null,
        string? browserInfo = null,
        string? correlationId = null,
        string? device = null,
        string? location = null,
        string? twoFactorCode = null,
        string? twoFactorRecoveryCode = null,
        string? deviceFingerprint = null,
        bool rememberTrustedDevice = false);
    Task<UserInfoDto?> GetUserInfoAsync(string loginInput, Guid? tenantId = null, InteractiveLoginMode mode = InteractiveLoginMode.Tenant);
    Task<UserInfoDto?> GetUserInfoByIdAsync(
        Guid userId,
        UserInfoSegments segments = UserInfoSegments.Profile | UserInfoSegments.Authorization);
    Task<InteractiveLoginResultDto> StartInteractiveLoginAsync(InteractiveLoginStartInput input);
    Task<InteractiveLoginResultDto> CompleteMfaAsync(CompleteMfaInput input);
    Task<InteractiveLoginResultDto> CompleteTrustDeviceAsync(CompleteTrustDeviceInput input);
    Task<PasskeyCeremonyOptionsDto> BeginInteractivePasskeyEnrollmentAsync(InteractivePasskeyOptionsInput input);
    Task<InteractiveLoginResultDto> CompleteInteractivePasskeyEnrollmentAsync(CompleteInteractivePasskeyInput input);
    Task<PasskeyCeremonyOptionsDto> BeginInteractivePasskeyAssertionAsync(InteractivePasskeyOptionsInput input);
    Task<InteractiveLoginResultDto> CompleteInteractivePasskeyAssertionAsync(CompleteInteractivePasskeyInput input);
    Task RequestPasswordResetAsync(PasswordResetRequestInput input);
    Task<PasswordResetValidateResultDto> ValidatePasswordResetAsync(PasswordResetValidateInput input);
    Task<PasswordResetCompleteResultDto> CompletePasswordResetAsync(PasswordResetCompleteInput input);
    Task<PasswordExpiredChangeResultDto> ChangeExpiredPasswordAsync(PasswordExpiredChangeInput input);
    Task RequestEmailVerificationAsync(EmailVerificationRequestInput input);
    Task<EmailVerificationResultDto> ConfirmEmailVerificationAsync(EmailVerificationConfirmInput input);
    Task ResendEmailVerificationAsync(EmailVerificationRequestInput input);
    Task<PublicInvitationValidateResultDto> ValidatePublicInvitationAsync(PublicInvitationValidateInput input);
    Task<PublicInvitationAcceptResultDto> AcceptPublicInvitationAsync(PublicInvitationAcceptInput input);
    Task<PublicInvitationAcceptResultDto> AcceptOwnershipInvitationAsync(OwnershipInvitationAcceptInput input);
}

public class CheckPasswordInput
{
    [Required]
    [MaxLength(256)]
    public string LoginInput { get; set; } = default!;

    [Required]
    [MaxLength(512)]
    public string Password { get; set; } = default!;

    public Guid? TenantId { get; set; }

    public InteractiveLoginMode Mode { get; set; } = InteractiveLoginMode.Tenant;

    [MaxLength(64)]
    public string? SessionId { get; set; }

    [MaxLength(128)]
    public string? ClientId { get; set; }

    [MaxLength(64)]
    public string? ClientIpAddress { get; set; }

    [MaxLength(1024)]
    public string? BrowserInfo { get; set; }

    [MaxLength(128)]
    public string? CorrelationId { get; set; }

    [MaxLength(128)]
    public string? Device { get; set; }

    [MaxLength(128)]
    public string? Location { get; set; }

    [MaxLength(32)]
    public string? TwoFactorCode { get; set; }

    [MaxLength(128)]
    public string? TwoFactorRecoveryCode { get; set; }

    [MaxLength(1024)]
    public string? DeviceFingerprint { get; set; }
    public bool RememberTrustedDevice { get; set; }
}

public class LoginResultDto
{
    public bool Succeeded { get; set; }
    public bool IsLockedOut { get; set; }
    public bool RequiresTwoFactor { get; set; }
    public bool RequiresMfaEnrollment { get; set; }
    public bool IsNotAllowed { get; set; }
    public bool ShouldChangePasswordOnNextLogin { get; set; }
    public string? FailureMessage { get; set; }
    public LoginRiskLevel RiskLevel { get; set; } = LoginRiskLevel.None;
    public int RiskScore { get; set; }
    public LoginRiskAction RiskAction { get; set; } = LoginRiskAction.Allow;
    public List<string> RiskReasons { get; set; } = [];
    public bool BlockedByRisk { get; set; }
}

public class UserInfoDto
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? PhoneNumber { get; set; }
    public Guid? TenantId { get; set; }
    public List<string> Roles { get; set; } = new();
    public List<Guid> RoleIds { get; set; } = new();
    public bool IsActive { get; set; }
    public bool IsLockedOut { get; set; }
    public string? ProfileVersion { get; set; }
    public string? AuthzVersion { get; set; }
}
