using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Account.Risk;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Identity.Application.SecuritySettings;

public sealed class HostIdentityOptionsAccessor : IOptions<IdentityOptions>
{
    private readonly HostIdentitySecurityPolicyRuntimeState _runtimeState;

    public HostIdentityOptionsAccessor(HostIdentitySecurityPolicyRuntimeState runtimeState)
    {
        _runtimeState = runtimeState;
    }

    public IdentityOptions Value
    {
        get
        {
            var settings = _runtimeState.GetCurrent();
            var options = new IdentityOptions();
            options.Password.RequiredLength = settings.Password.RequiredLength;
            options.Password.RequireDigit = settings.Password.RequireDigit;
            options.Password.RequireLowercase = settings.Password.RequireLowercase;
            options.Password.RequireUppercase = settings.Password.RequireUppercase;
            options.Password.RequireNonAlphanumeric = settings.Password.RequireNonAlphanumeric;
            options.User.RequireUniqueEmail = true;
            options.Lockout.AllowedForNewUsers = settings.Lockout.AllowedForNewUsers;
            options.Lockout.MaxFailedAccessAttempts = settings.Lockout.MaxFailedAccessAttempts;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(settings.Lockout.DefaultLockoutMinutes);
            return options;
        }
    }
}

public sealed class HostLoginRiskOptionsAccessor : IOptions<LoginRiskOptions>
{
    private readonly HostIdentitySecurityPolicyRuntimeState _runtimeState;
    private readonly HostIdentitySecurityStaticOptions _staticOptions;

    public HostLoginRiskOptionsAccessor(
        HostIdentitySecurityPolicyRuntimeState runtimeState,
        HostIdentitySecurityStaticOptions staticOptions)
    {
        _runtimeState = runtimeState;
        _staticOptions = staticOptions;
    }

    public LoginRiskOptions Value
    {
        get
        {
            var settings = _runtimeState.GetCurrent();
            return new LoginRiskOptions
            {
                Enabled = settings.LoginRisk.Enabled,
                Mode = settings.LoginRisk.Mode,
                LowThreshold = settings.LoginRisk.LowThreshold,
                HighThreshold = settings.LoginRisk.HighThreshold,
                NewDeviceWeight = settings.LoginRisk.NewDeviceWeight,
                NewIpWeight = settings.LoginRisk.NewIpWeight,
                GeoShiftWeight = _staticOptions.LoginRiskGeoSignalTrusted
                    ? settings.LoginRisk.GeoShiftWeight
                    : 0,
                GeoSignalTrusted = _staticOptions.LoginRiskGeoSignalTrusted,
                FailureBurstWeight = settings.LoginRisk.FailureBurstWeight,
                FailureBurstWindow = TimeSpan.FromMinutes(settings.LoginRisk.FailureBurstWindowMinutes),
                FailureBurstThreshold = settings.LoginRisk.FailureBurstThreshold,
                HistoryWindow = TimeSpan.FromDays(settings.LoginRisk.HistoryWindowDays),
                TrustedDeviceDays = settings.LoginRisk.TrustedDeviceDays,
                HeaderCountryCodeName = _staticOptions.LoginRiskHeaderCountryCodeName
            };
        }
    }
}

public sealed class HostInteractiveLoginOptionsAccessor : IOptions<InteractiveLoginOptions>
{
    private readonly HostIdentitySecurityPolicyRuntimeState _runtimeState;

    public HostInteractiveLoginOptionsAccessor(HostIdentitySecurityPolicyRuntimeState runtimeState)
    {
        _runtimeState = runtimeState;
    }

    public InteractiveLoginOptions Value => new()
    {
        ChallengeTtlSeconds = _runtimeState.GetCurrent().InteractiveLogin.ChallengeTtlSeconds
    };
}

public sealed class HostLoginAttemptProtectionOptionsAccessor : IOptions<LoginAttemptProtectionOptions>
{
    private readonly HostIdentitySecurityPolicyRuntimeState _runtimeState;

    public HostLoginAttemptProtectionOptionsAccessor(HostIdentitySecurityPolicyRuntimeState runtimeState)
    {
        _runtimeState = runtimeState;
    }

    public LoginAttemptProtectionOptions Value
    {
        get
        {
            var settings = _runtimeState.GetCurrent();
            return new LoginAttemptProtectionOptions
            {
                Enabled = settings.LoginAttemptProtection.Enabled,
                RequestLimitPerWindow = settings.LoginAttemptProtection.RequestLimitPerWindow,
                RequestLimitWindowSeconds = settings.LoginAttemptProtection.RequestLimitWindowSeconds
            };
        }
    }
}

public sealed class HostPasswordResetOptionsAccessor : IOptions<PasswordResetOptions>
{
    private readonly HostIdentitySecurityPolicyRuntimeState _runtimeState;
    private readonly HostIdentitySecurityStaticOptions _staticOptions;

    public HostPasswordResetOptionsAccessor(
        HostIdentitySecurityPolicyRuntimeState runtimeState,
        HostIdentitySecurityStaticOptions staticOptions)
    {
        _runtimeState = runtimeState;
        _staticOptions = staticOptions;
    }

    public PasswordResetOptions Value
    {
        get
        {
            var settings = _runtimeState.GetCurrent();
            return new PasswordResetOptions
            {
                Enabled = settings.PasswordReset.Enabled,
                TokenTtlMinutes = _staticOptions.PasswordResetTokenTtlMinutes,
                TemplateName = _staticOptions.PasswordResetTemplateName,
                RequestLimitPerWindow = settings.PasswordReset.RequestLimitPerWindow,
                RequestLimitWindowSeconds = settings.PasswordReset.RequestLimitWindowSeconds,
                AllowedBaseUrls = [.. _staticOptions.PasswordResetAllowedBaseUrls]
            };
        }
    }
}

public sealed class HostEmailVerificationOptionsAccessor : IOptions<EmailVerificationOptions>
{
    private readonly HostIdentitySecurityPolicyRuntimeState _runtimeState;
    private readonly HostIdentitySecurityStaticOptions _staticOptions;

    public HostEmailVerificationOptionsAccessor(
        HostIdentitySecurityPolicyRuntimeState runtimeState,
        HostIdentitySecurityStaticOptions staticOptions)
    {
        _runtimeState = runtimeState;
        _staticOptions = staticOptions;
    }

    public EmailVerificationOptions Value => new()
    {
        Enabled = _runtimeState.GetCurrent().EmailVerification.Enabled,
        TemplateName = _staticOptions.EmailVerificationTemplateName,
        AllowedBaseUrls = [.. _staticOptions.EmailVerificationAllowedBaseUrls]
    };
}

public sealed class HostPublicInvitationManagementOptionsAccessor : IOptions<PublicInvitationManagementOptions>
{
    private readonly HostIdentitySecurityPolicyRuntimeState _runtimeState;
    private readonly HostIdentitySecurityStaticOptions _staticOptions;

    public HostPublicInvitationManagementOptionsAccessor(
        HostIdentitySecurityPolicyRuntimeState runtimeState,
        HostIdentitySecurityStaticOptions staticOptions)
    {
        _runtimeState = runtimeState;
        _staticOptions = staticOptions;
    }

    public PublicInvitationManagementOptions Value
    {
        get
        {
            var settings = _runtimeState.GetCurrent();
            return new PublicInvitationManagementOptions
            {
                Enabled = settings.PublicInvitationManagement.Enabled,
                TokenTtlHours = settings.PublicInvitationManagement.TokenTtlHours,
                TemplateName = _staticOptions.PublicInvitationTemplateName,
                Subject = _staticOptions.PublicInvitationSubject,
                AllowedBaseUrls = [.. _staticOptions.PublicInvitationAllowedBaseUrls]
            };
        }
    }
}
