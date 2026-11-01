using System.ComponentModel.DataAnnotations;
using Identity.Domain.Shared.Account;

namespace Identity.Application.Contracts.Account;

public class InteractiveLoginStartInput
{
    public InteractiveLoginMode Mode { get; set; } = InteractiveLoginMode.Tenant;

    public Guid? TenantId { get; set; }

    [Required]
    [MaxLength(256)]
    public string LoginInput { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string Password { get; set; } = string.Empty;

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

    [MaxLength(1024)]
    public string? DeviceFingerprint { get; set; }

    public bool PromptForTrustDevice { get; set; }
}

public class InteractiveChallengeInput
{
    public InteractiveLoginMode Mode { get; set; } = InteractiveLoginMode.Tenant;

    public Guid? TenantId { get; set; }

    public Guid? ChallengeId { get; set; }

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

    [MaxLength(1024)]
    public string? DeviceFingerprint { get; set; }
}

public class CompleteMfaInput : InteractiveChallengeInput
{
    [MaxLength(32)]
    public string? TwoFactorCode { get; set; }

    [MaxLength(128)]
    public string? TwoFactorRecoveryCode { get; set; }

    public bool RememberTrustedDevice { get; set; }
}

public class CompleteTrustDeviceInput : InteractiveChallengeInput
{
    public bool TrustDevice { get; set; }
}

public class InteractiveLoginResultDto
{
    public InteractiveLoginStep Step { get; set; } = InteractiveLoginStep.Failed;
    public LoginResultDto LoginResult { get; set; } = new();
    public string? Message { get; set; }
    public Guid? ChallengeId { get; set; }
    public DateTimeOffset? ChallengeExpiresAtUtc { get; set; }
    public bool PasskeyAvailable { get; set; }
    public bool AuthenticatorCodeAvailable { get; set; }
    public bool RecoveryCodeAvailable { get; set; }
    public bool CodeBasedMfaAvailable => AuthenticatorCodeAvailable || RecoveryCodeAvailable;
}

public class PasswordResetRequestInput
{
    [Required]
    public Guid TenantId { get; set; }

    [Required]
    [MaxLength(256)]
    public string LoginInput { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string ResetBaseUrl { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? ClientIpAddress { get; set; }

    [MaxLength(1024)]
    public string? BrowserInfo { get; set; }

    [MaxLength(128)]
    public string? CorrelationId { get; set; }
}

public class PasswordResetValidateInput
{
    [Required]
    public Guid TenantId { get; set; }

    [Required]
    public Guid UserId { get; set; }

    [Required]
    [MaxLength(2048)]
    public string Token { get; set; } = string.Empty;
}

public class PasswordResetValidateResultDto
{
    public PasswordResetValidationStatus Status { get; set; } = PasswordResetValidationStatus.Invalid;
    public bool IsValid => Status == PasswordResetValidationStatus.Valid;
    public string? Message { get; set; }
}

public class PasswordResetCompleteInput : PasswordResetValidateInput
{
    [Required]
    [MaxLength(256)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class PasswordResetCompleteResultDto
{
    public bool Succeeded { get; set; }
    public PasswordResetValidationStatus TokenStatus { get; set; } = PasswordResetValidationStatus.Invalid;
    public List<string> Errors { get; set; } = [];
}

public class PasswordExpiredChangeInput
{
    public InteractiveLoginMode Mode { get; set; } = InteractiveLoginMode.Tenant;

    public Guid? TenantId { get; set; }

    public Guid? ChallengeId { get; set; }

    [Required]
    [MaxLength(256)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class PasswordExpiredChangeResultDto
{
    public bool Succeeded { get; set; }
    public List<string> Errors { get; set; } = [];
}

public class EmailVerificationRequestInput
{
    [Required]
    public Guid TenantId { get; set; }

    [Required]
    [MaxLength(256)]
    public string LoginInput { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string VerificationBaseUrl { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? ClientIpAddress { get; set; }

    [MaxLength(1024)]
    public string? BrowserInfo { get; set; }

    [MaxLength(128)]
    public string? CorrelationId { get; set; }
}

public class EmailVerificationConfirmInput
{
    [Required]
    public Guid TenantId { get; set; }

    [Required]
    public Guid UserId { get; set; }

    [Required]
    [MaxLength(2048)]
    public string Token { get; set; } = string.Empty;
}

public class EmailVerificationResultDto
{
    public bool Succeeded { get; set; }
    public string? Message { get; set; }
}

public class PasswordResetOptions
{
    public bool Enabled { get; set; } = true;
    public int TokenTtlMinutes { get; set; } = 15;
    public string TemplateName { get; set; } = "Identity.PasswordResetLink";
    public int RequestLimitPerWindow { get; set; } = 5;
    public int RequestLimitWindowSeconds { get; set; } = 300;
    public List<string> AllowedBaseUrls { get; set; } = [];
}

public class EmailVerificationOptions
{
    public bool Enabled { get; set; } = true;
    public string TemplateName { get; set; } = "Identity.EmailVerificationLink";
    public List<string> AllowedBaseUrls { get; set; } = [];
}

public class PublicInvitationManagementOptions
{
    public bool Enabled { get; set; } = true;
    public int TokenTtlHours { get; set; } = 168;
    public string TemplateName { get; set; } = "Identity.PublicInvitationLink";
    public string Subject { get; set; } = "Your organization invited you to join";
    public List<string> AllowedBaseUrls { get; set; } = [];

    /// <summary>
    /// Maximum number of rows accepted in a single bulk-invitation request
    /// (validate or create). Requests above this are rejected before any row is
    /// processed. Bounded to a sane ceiling regardless of configuration.
    /// </summary>
    public int MaxBatchSize { get; set; } = 200;
}

public class InteractiveLoginOptions
{
    public int ChallengeTtlSeconds { get; set; } = 300;
}

public class LoginAttemptProtectionOptions
{
    public bool Enabled { get; set; } = true;
    public int RequestLimitPerWindow { get; set; } = 15;
    public int RequestLimitWindowSeconds { get; set; } = 300;
}
