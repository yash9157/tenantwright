using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Identity.Application.Contracts.SecurityLogs;

namespace Identity.Application.Contracts.Account;

public class ProfileDto
{
    public string UserName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? PendingEmail { get; set; }
    public string? Name { get; set; }
    public string? Surname { get; set; }
    public string? PhoneNumber { get; set; }
    public bool IsExternal { get; set; }
    public bool HasPassword { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public string? ConcurrencyStamp { get; set; }

    /// <summary>Number of the caller's currently active sessions (tenant-scoped).</summary>
    public int ActiveSessionCount { get; set; }

    /// <summary>Start time of the caller's most recent active session (≈ most recent sign-in), if any.</summary>
    public DateTime? LastSignInAtUtc { get; set; }
}

public class UpdateProfileDto
{
    public string UserName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? EmailConfirmationBaseUrl { get; set; }
    public string? CurrentPassword { get; set; }
    public string? TwoFactorCode { get; set; }
    public string? TwoFactorRecoveryCode { get; set; }
    public string? Name { get; set; }
    public string? Surname { get; set; }
    public string? PhoneNumber { get; set; }
    public string? ConcurrencyStamp { get; set; }
}

public class ChangePasswordInput
{
    public string? CurrentPassword { get; set; }
    public string NewPassword { get; set; } = default!;
}

public class MfaStatusDto
{
    public bool IsEnabled { get; set; }
    public int RecoveryCodesLeft { get; set; }
}

public class MfaSetupDto : MfaStatusDto
{
    public string SharedKey { get; set; } = string.Empty;
    public string AuthenticatorUri { get; set; } = string.Empty;
    public List<string> RecoveryCodes { get; set; } = [];
}

public class NotificationChannelPreferencesDto
{
    public bool EmailEnabled { get; set; } = true;
}

public class SecurityAlertPreferencesDto
{
    public bool NewDeviceSignInAlertEnabled { get; set; } = true;
    public bool RiskBlockedAlertEnabled { get; set; } = true;
    public bool AdminSessionRevokedAlertEnabled { get; set; } = true;
    public bool MfaChangedAlertEnabled { get; set; } = true;
}

public class UserNotificationSecurityPreferencesDto
{
    public NotificationChannelPreferencesDto Channels { get; set; } = new();
    public SecurityAlertPreferencesDto SecurityAlerts { get; set; } = new();
}

public class UpdateUserNotificationSecurityPreferencesInput
{
    public NotificationChannelPreferencesDto Channels { get; set; } = new();
    public SecurityAlertPreferencesDto SecurityAlerts { get; set; } = new();
}

public class UserSecurityAlertPreferenceStateDto
{
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
    public string AlertKey { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

public class EnableMfaInput
{
    public string Code { get; set; } = default!;
}

/// <summary>
/// Re-authentication material required before a user can reveal or replace a
/// factor that protects their account. The value is used only to authorize the
/// current request and is never persisted or returned.
/// </summary>
public class MfaSensitiveOperationProofInput
{
    [Required]
    [MaxLength(256)]
    public string CurrentPassword { get; set; } = string.Empty;
}

public class ProfileRiskDecisionListInput
{
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 20;
    public bool IncludeFalsePositive { get; set; }
}

public interface IProfileAppService
{
    Task<ProfileDto> GetAsync();
    Task<ProfileDto> UpdateAsync(UpdateProfileDto input);
    Task<ProfileDto> ResendPendingEmailConfirmationAsync();
    Task<ProfileDto> CancelPendingEmailChangeAsync();
    Task<UserNotificationSecurityPreferencesDto> GetPreferencesAsync();
    Task<UserNotificationSecurityPreferencesDto> UpdatePreferencesAsync(UpdateUserNotificationSecurityPreferencesInput input);
    Task ChangePasswordAsync(ChangePasswordInput input);
    Task<MfaStatusDto> GetMfaStatusAsync();
    Task<MfaSetupDto> GenerateMfaSetupAsync(MfaSensitiveOperationProofInput input);
    Task<MfaSetupDto> EnableMfaAsync(EnableMfaInput input);
    Task<MfaSetupDto> RegenerateRecoveryCodesAsync(MfaSensitiveOperationProofInput input);
    Task DisableMfaAsync(MfaSensitiveOperationProofInput input);
    Task<PagedResultDto<SecurityLogDto>> GetMySecurityLogsPagedAsync(SecurityLogListInput input);
    Task<PagedResultDto<SecurityLogDto>> GetMyRiskDecisionsPagedAsync(ProfileRiskDecisionListInput input);
    Task MarkMyRiskDecisionFalsePositiveAsync(Guid securityLogId);
}

public interface IInternalProfilePreferenceLookupAppService
{
    Task<UserSecurityAlertPreferenceStateDto> GetAlertPreferenceAsync(Guid userId, string alertKey);
}
