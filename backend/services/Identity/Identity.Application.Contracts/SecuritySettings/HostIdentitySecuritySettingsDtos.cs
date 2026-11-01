using System.ComponentModel.DataAnnotations;
using Identity.Domain.Shared.Account.Risk;

namespace Identity.Application.Contracts.SecuritySettings;

public sealed class HostIdentitySecurityPasswordSettingsDto
{
    [Range(1, 256)]
    public int RequiredLength { get; set; } = 8;

    public bool RequireDigit { get; set; } = true;

    public bool RequireLowercase { get; set; } = true;

    public bool RequireUppercase { get; set; } = true;

    public bool RequireNonAlphanumeric { get; set; } = true;
}

public sealed class HostIdentitySecurityLockoutSettingsDto
{
    public bool AllowedForNewUsers { get; set; } = true;

    [Range(1, 100)]
    public int MaxFailedAccessAttempts { get; set; } = 5;

    [Range(1, 1440)]
    public int DefaultLockoutMinutes { get; set; } = 15;
}

public sealed class HostIdentityLoginRiskSettingsDto
{
    public bool Enabled { get; set; } = true;

    public LoginRiskMode Mode { get; set; } = LoginRiskMode.Observe;

    public int LowThreshold { get; set; } = 40;

    public int HighThreshold { get; set; } = 70;

    public int NewDeviceWeight { get; set; } = 25;

    public int NewIpWeight { get; set; } = 20;

    public int GeoShiftWeight { get; set; } = 30;

    public int FailureBurstWeight { get; set; } = 25;

    [Range(1, 1440)]
    public int FailureBurstWindowMinutes { get; set; } = 15;

    [Range(1, 1000)]
    public int FailureBurstThreshold { get; set; } = 3;

    [Range(1, 3650)]
    public int HistoryWindowDays { get; set; } = 30;

    [Range(1, 3650)]
    public int TrustedDeviceDays { get; set; } = 30;
}

public sealed class HostIdentityInteractiveLoginSettingsDto
{
    [Range(30, 900)]
    public int ChallengeTtlSeconds { get; set; } = 300;
}

public sealed class HostIdentityLoginAttemptProtectionSettingsDto
{
    public bool Enabled { get; set; } = true;

    [Range(1, 500)]
    public int RequestLimitPerWindow { get; set; } = 15;

    [Range(5, 3600)]
    public int RequestLimitWindowSeconds { get; set; } = 300;
}

public sealed class HostIdentityPasswordResetSettingsDto
{
    public bool Enabled { get; set; } = true;

    [Range(1, 100)]
    public int RequestLimitPerWindow { get; set; } = 5;

    [Range(30, 3600)]
    public int RequestLimitWindowSeconds { get; set; } = 300;
}

public sealed class HostIdentityEmailVerificationSettingsDto
{
    public bool Enabled { get; set; } = true;
}

public sealed class HostIdentityPublicInvitationManagementSettingsDto
{
    public bool Enabled { get; set; } = true;

    [Range(1, 720)]
    public int TokenTtlHours { get; set; } = 168;
}

public sealed class HostIdentitySecuritySettingsDto
{
    public string? ConcurrencyStamp { get; set; }

    [Required]
    public HostIdentitySecurityPasswordSettingsDto Password { get; set; } = new();

    [Required]
    public HostIdentitySecurityLockoutSettingsDto Lockout { get; set; } = new();

    [Required]
    public HostIdentityLoginRiskSettingsDto LoginRisk { get; set; } = new();

    [Required]
    public HostIdentityInteractiveLoginSettingsDto InteractiveLogin { get; set; } = new();

    [Required]
    public HostIdentityLoginAttemptProtectionSettingsDto LoginAttemptProtection { get; set; } = new();

    [Required]
    public HostIdentityPasswordResetSettingsDto PasswordReset { get; set; } = new();

    [Required]
    public HostIdentityEmailVerificationSettingsDto EmailVerification { get; set; } = new();

    [Required]
    public HostIdentityPublicInvitationManagementSettingsDto PublicInvitationManagement { get; set; } = new();
}

public sealed class UpdateHostIdentitySecuritySettingsInput
{
    [Required]
    public string ConcurrencyStamp { get; set; } = string.Empty;

    [Required]
    public HostIdentitySecurityPasswordSettingsDto Password { get; set; } = new();

    [Required]
    public HostIdentitySecurityLockoutSettingsDto Lockout { get; set; } = new();

    [Required]
    public HostIdentityLoginRiskSettingsDto LoginRisk { get; set; } = new();

    [Required]
    public HostIdentityInteractiveLoginSettingsDto InteractiveLogin { get; set; } = new();

    [Required]
    public HostIdentityLoginAttemptProtectionSettingsDto LoginAttemptProtection { get; set; } = new();

    [Required]
    public HostIdentityPasswordResetSettingsDto PasswordReset { get; set; } = new();

    [Required]
    public HostIdentityEmailVerificationSettingsDto EmailVerification { get; set; } = new();

    [Required]
    public HostIdentityPublicInvitationManagementSettingsDto PublicInvitationManagement { get; set; } = new();
}

/// <summary>
/// Additive governed update surface. Existing callers can continue using the
/// legacy update while the console supplies a human audit reason.
/// </summary>
public sealed class GovernedHostIdentitySecuritySettingsUpdateInput
{
    [Required]
    public UpdateHostIdentitySecuritySettingsInput Settings { get; set; } = new();

    [Required]
    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class HostIdentitySecurityPolicyImpactDto
{
    public List<string> ChangedSections { get; set; } = [];
    public DateTime EffectiveAtUtc { get; set; }
    public bool AffectsFutureSignIns { get; set; }
    /// <summary>
    /// Existing sessions remain valid. Policy changes take effect on future
    /// evaluations and do not silently revoke live staff access.
    /// </summary>
    public bool ActiveSessionsRemainValid { get; set; }
}
