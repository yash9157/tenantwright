using Identity.Application.Contracts.SecuritySettings;
using Identity.Domain.SecuritySettings;

namespace Identity.Application.SecuritySettings;

public static class HostIdentitySecuritySettingsMapper
{
    public static HostIdentitySecuritySettingsDto Clone(HostIdentitySecuritySettingsDto source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new HostIdentitySecuritySettingsDto
        {
            ConcurrencyStamp = source.ConcurrencyStamp,
            Password = new HostIdentitySecurityPasswordSettingsDto
            {
                RequiredLength = source.Password.RequiredLength,
                RequireDigit = source.Password.RequireDigit,
                RequireLowercase = source.Password.RequireLowercase,
                RequireUppercase = source.Password.RequireUppercase,
                RequireNonAlphanumeric = source.Password.RequireNonAlphanumeric
            },
            Lockout = new HostIdentitySecurityLockoutSettingsDto
            {
                AllowedForNewUsers = source.Lockout.AllowedForNewUsers,
                MaxFailedAccessAttempts = source.Lockout.MaxFailedAccessAttempts,
                DefaultLockoutMinutes = source.Lockout.DefaultLockoutMinutes
            },
            LoginRisk = new HostIdentityLoginRiskSettingsDto
            {
                Enabled = source.LoginRisk.Enabled,
                Mode = source.LoginRisk.Mode,
                LowThreshold = source.LoginRisk.LowThreshold,
                HighThreshold = source.LoginRisk.HighThreshold,
                NewDeviceWeight = source.LoginRisk.NewDeviceWeight,
                NewIpWeight = source.LoginRisk.NewIpWeight,
                GeoShiftWeight = source.LoginRisk.GeoShiftWeight,
                FailureBurstWeight = source.LoginRisk.FailureBurstWeight,
                FailureBurstWindowMinutes = source.LoginRisk.FailureBurstWindowMinutes,
                FailureBurstThreshold = source.LoginRisk.FailureBurstThreshold,
                HistoryWindowDays = source.LoginRisk.HistoryWindowDays,
                TrustedDeviceDays = source.LoginRisk.TrustedDeviceDays
            },
            InteractiveLogin = new HostIdentityInteractiveLoginSettingsDto
            {
                ChallengeTtlSeconds = source.InteractiveLogin.ChallengeTtlSeconds
            },
            LoginAttemptProtection = new HostIdentityLoginAttemptProtectionSettingsDto
            {
                Enabled = source.LoginAttemptProtection.Enabled,
                RequestLimitPerWindow = source.LoginAttemptProtection.RequestLimitPerWindow,
                RequestLimitWindowSeconds = source.LoginAttemptProtection.RequestLimitWindowSeconds
            },
            PasswordReset = new HostIdentityPasswordResetSettingsDto
            {
                Enabled = source.PasswordReset.Enabled,
                RequestLimitPerWindow = source.PasswordReset.RequestLimitPerWindow,
                RequestLimitWindowSeconds = source.PasswordReset.RequestLimitWindowSeconds
            },
            EmailVerification = new HostIdentityEmailVerificationSettingsDto
            {
                Enabled = source.EmailVerification.Enabled
            },
            PublicInvitationManagement = new HostIdentityPublicInvitationManagementSettingsDto
            {
                Enabled = source.PublicInvitationManagement.Enabled,
                TokenTtlHours = source.PublicInvitationManagement.TokenTtlHours
            }
        };
    }

    public static HostIdentitySecuritySettingsDto FromPolicy(HostIdentitySecurityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return new HostIdentitySecuritySettingsDto
        {
            ConcurrencyStamp = policy.ConcurrencyStamp,
            Password = new HostIdentitySecurityPasswordSettingsDto
            {
                RequiredLength = policy.PasswordRequiredLength,
                RequireDigit = policy.PasswordRequireDigit,
                RequireLowercase = policy.PasswordRequireLowercase,
                RequireUppercase = policy.PasswordRequireUppercase,
                RequireNonAlphanumeric = policy.PasswordRequireNonAlphanumeric
            },
            Lockout = new HostIdentitySecurityLockoutSettingsDto
            {
                AllowedForNewUsers = policy.LockoutAllowedForNewUsers,
                MaxFailedAccessAttempts = policy.LockoutMaxFailedAccessAttempts,
                DefaultLockoutMinutes = policy.LockoutDefaultLockoutMinutes
            },
            LoginRisk = new HostIdentityLoginRiskSettingsDto
            {
                Enabled = policy.LoginRiskEnabled,
                Mode = policy.LoginRiskMode,
                LowThreshold = policy.LoginRiskLowThreshold,
                HighThreshold = policy.LoginRiskHighThreshold,
                NewDeviceWeight = policy.LoginRiskNewDeviceWeight,
                NewIpWeight = policy.LoginRiskNewIpWeight,
                GeoShiftWeight = policy.LoginRiskGeoShiftWeight,
                FailureBurstWeight = policy.LoginRiskFailureBurstWeight,
                FailureBurstWindowMinutes = policy.LoginRiskFailureBurstWindowMinutes,
                FailureBurstThreshold = policy.LoginRiskFailureBurstThreshold,
                HistoryWindowDays = policy.LoginRiskHistoryWindowDays,
                TrustedDeviceDays = policy.LoginRiskTrustedDeviceDays
            },
            InteractiveLogin = new HostIdentityInteractiveLoginSettingsDto
            {
                ChallengeTtlSeconds = policy.InteractiveLoginChallengeTtlSeconds
            },
            LoginAttemptProtection = new HostIdentityLoginAttemptProtectionSettingsDto
            {
                Enabled = policy.LoginAttemptProtectionEnabled,
                RequestLimitPerWindow = policy.LoginAttemptProtectionRequestLimitPerWindow,
                RequestLimitWindowSeconds = policy.LoginAttemptProtectionRequestLimitWindowSeconds
            },
            PasswordReset = new HostIdentityPasswordResetSettingsDto
            {
                Enabled = policy.PasswordResetEnabled,
                RequestLimitPerWindow = policy.PasswordResetRequestLimitPerWindow,
                RequestLimitWindowSeconds = policy.PasswordResetRequestLimitWindowSeconds
            },
            EmailVerification = new HostIdentityEmailVerificationSettingsDto
            {
                Enabled = policy.EmailVerificationEnabled
            },
            PublicInvitationManagement = new HostIdentityPublicInvitationManagementSettingsDto
            {
                Enabled = policy.PublicInvitationManagementEnabled,
                TokenTtlHours = policy.PublicInvitationManagementTokenTtlHours
            }
        };
    }

    public static HostIdentitySecuritySettingsDto FromInput(UpdateHostIdentitySecuritySettingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new HostIdentitySecuritySettingsDto
        {
            ConcurrencyStamp = input.ConcurrencyStamp,
            Password = new HostIdentitySecurityPasswordSettingsDto
            {
                RequiredLength = input.Password.RequiredLength,
                RequireDigit = input.Password.RequireDigit,
                RequireLowercase = input.Password.RequireLowercase,
                RequireUppercase = input.Password.RequireUppercase,
                RequireNonAlphanumeric = input.Password.RequireNonAlphanumeric
            },
            Lockout = new HostIdentitySecurityLockoutSettingsDto
            {
                AllowedForNewUsers = input.Lockout.AllowedForNewUsers,
                MaxFailedAccessAttempts = input.Lockout.MaxFailedAccessAttempts,
                DefaultLockoutMinutes = input.Lockout.DefaultLockoutMinutes
            },
            LoginRisk = new HostIdentityLoginRiskSettingsDto
            {
                Enabled = input.LoginRisk.Enabled,
                Mode = input.LoginRisk.Mode,
                LowThreshold = input.LoginRisk.LowThreshold,
                HighThreshold = input.LoginRisk.HighThreshold,
                NewDeviceWeight = input.LoginRisk.NewDeviceWeight,
                NewIpWeight = input.LoginRisk.NewIpWeight,
                GeoShiftWeight = input.LoginRisk.GeoShiftWeight,
                FailureBurstWeight = input.LoginRisk.FailureBurstWeight,
                FailureBurstWindowMinutes = input.LoginRisk.FailureBurstWindowMinutes,
                FailureBurstThreshold = input.LoginRisk.FailureBurstThreshold,
                HistoryWindowDays = input.LoginRisk.HistoryWindowDays,
                TrustedDeviceDays = input.LoginRisk.TrustedDeviceDays
            },
            InteractiveLogin = new HostIdentityInteractiveLoginSettingsDto
            {
                ChallengeTtlSeconds = input.InteractiveLogin.ChallengeTtlSeconds
            },
            LoginAttemptProtection = new HostIdentityLoginAttemptProtectionSettingsDto
            {
                Enabled = input.LoginAttemptProtection.Enabled,
                RequestLimitPerWindow = input.LoginAttemptProtection.RequestLimitPerWindow,
                RequestLimitWindowSeconds = input.LoginAttemptProtection.RequestLimitWindowSeconds
            },
            PasswordReset = new HostIdentityPasswordResetSettingsDto
            {
                Enabled = input.PasswordReset.Enabled,
                RequestLimitPerWindow = input.PasswordReset.RequestLimitPerWindow,
                RequestLimitWindowSeconds = input.PasswordReset.RequestLimitWindowSeconds
            },
            EmailVerification = new HostIdentityEmailVerificationSettingsDto
            {
                Enabled = input.EmailVerification.Enabled
            },
            PublicInvitationManagement = new HostIdentityPublicInvitationManagementSettingsDto
            {
                Enabled = input.PublicInvitationManagement.Enabled,
                TokenTtlHours = input.PublicInvitationManagement.TokenTtlHours
            }
        };
    }

    public static HostIdentitySecurityPolicy CreatePolicy(HostIdentitySecuritySettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var policy = new HostIdentitySecurityPolicy(HostIdentitySecurityPolicy.SingletonId);
        Apply(policy, settings);
        return policy;
    }

    public static void Apply(HostIdentitySecurityPolicy policy, HostIdentitySecuritySettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(settings);

        policy.Apply(
            settings.Password.RequiredLength,
            settings.Password.RequireDigit,
            settings.Password.RequireLowercase,
            settings.Password.RequireUppercase,
            settings.Password.RequireNonAlphanumeric,
            settings.Lockout.AllowedForNewUsers,
            settings.Lockout.MaxFailedAccessAttempts,
            settings.Lockout.DefaultLockoutMinutes,
            settings.LoginRisk.Enabled,
            settings.LoginRisk.Mode,
            settings.LoginRisk.LowThreshold,
            settings.LoginRisk.HighThreshold,
            settings.LoginRisk.NewDeviceWeight,
            settings.LoginRisk.NewIpWeight,
            settings.LoginRisk.GeoShiftWeight,
            settings.LoginRisk.FailureBurstWeight,
            settings.LoginRisk.FailureBurstWindowMinutes,
            settings.LoginRisk.FailureBurstThreshold,
            settings.LoginRisk.HistoryWindowDays,
            settings.LoginRisk.TrustedDeviceDays,
            settings.InteractiveLogin.ChallengeTtlSeconds,
            settings.LoginAttemptProtection.Enabled,
            settings.LoginAttemptProtection.RequestLimitPerWindow,
            settings.LoginAttemptProtection.RequestLimitWindowSeconds,
            settings.PasswordReset.Enabled,
            settings.PasswordReset.RequestLimitPerWindow,
            settings.PasswordReset.RequestLimitWindowSeconds,
            settings.EmailVerification.Enabled,
            settings.PublicInvitationManagement.Enabled,
            settings.PublicInvitationManagement.TokenTtlHours);
    }
}
