using Identity.Domain.Shared.Account.Risk;
using Shared;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Identity.Domain.SecuritySettings;

public sealed class HostIdentitySecurityPolicy : AggregateRoot<Guid>, IMultiTenant
{
    public static readonly Guid SingletonId = Guid.Parse("7b3d35ef-9523-4a84-b2ea-df40f808ee11");

    public Guid? TenantId { get; private set; }

    public int PasswordRequiredLength { get; private set; } = 8;
    public bool PasswordRequireDigit { get; private set; } = true;
    public bool PasswordRequireLowercase { get; private set; } = true;
    public bool PasswordRequireUppercase { get; private set; } = true;
    public bool PasswordRequireNonAlphanumeric { get; private set; } = true;

    public bool LockoutAllowedForNewUsers { get; private set; } = true;
    public int LockoutMaxFailedAccessAttempts { get; private set; } = 5;
    public int LockoutDefaultLockoutMinutes { get; private set; } = 15;

    public bool LoginRiskEnabled { get; private set; } = true;
    public LoginRiskMode LoginRiskMode { get; private set; } = LoginRiskMode.Observe;
    public int LoginRiskLowThreshold { get; private set; } = 40;
    public int LoginRiskHighThreshold { get; private set; } = 70;
    public int LoginRiskNewDeviceWeight { get; private set; } = 25;
    public int LoginRiskNewIpWeight { get; private set; } = 20;
    public int LoginRiskGeoShiftWeight { get; private set; } = 30;
    public int LoginRiskFailureBurstWeight { get; private set; } = 25;
    public int LoginRiskFailureBurstWindowMinutes { get; private set; } = 15;
    public int LoginRiskFailureBurstThreshold { get; private set; } = 3;
    public int LoginRiskHistoryWindowDays { get; private set; } = 30;
    public int LoginRiskTrustedDeviceDays { get; private set; } = 30;

    public int InteractiveLoginChallengeTtlSeconds { get; private set; } = 300;

    public bool LoginAttemptProtectionEnabled { get; private set; } = true;
    public int LoginAttemptProtectionRequestLimitPerWindow { get; private set; } = 15;
    public int LoginAttemptProtectionRequestLimitWindowSeconds { get; private set; } = 300;

    public bool PasswordResetEnabled { get; private set; } = true;
    public int PasswordResetRequestLimitPerWindow { get; private set; } = 5;
    public int PasswordResetRequestLimitWindowSeconds { get; private set; } = 300;

    public bool EmailVerificationEnabled { get; private set; } = true;

    public bool PublicInvitationManagementEnabled { get; private set; } = true;
    public int PublicInvitationManagementTokenTtlHours { get; private set; } = 168;

    private HostIdentitySecurityPolicy()
        : base(SingletonId)
    {
        TenantId = null;
    }

    public HostIdentitySecurityPolicy(Guid id)
        : base(id)
    {
        TenantId = null;
    }

    public void Apply(
        int passwordRequiredLength,
        bool passwordRequireDigit,
        bool passwordRequireLowercase,
        bool passwordRequireUppercase,
        bool passwordRequireNonAlphanumeric,
        bool lockoutAllowedForNewUsers,
        int lockoutMaxFailedAccessAttempts,
        int lockoutDefaultLockoutMinutes,
        bool loginRiskEnabled,
        LoginRiskMode loginRiskMode,
        int loginRiskLowThreshold,
        int loginRiskHighThreshold,
        int loginRiskNewDeviceWeight,
        int loginRiskNewIpWeight,
        int loginRiskGeoShiftWeight,
        int loginRiskFailureBurstWeight,
        int loginRiskFailureBurstWindowMinutes,
        int loginRiskFailureBurstThreshold,
        int loginRiskHistoryWindowDays,
        int loginRiskTrustedDeviceDays,
        int interactiveLoginChallengeTtlSeconds,
        bool loginAttemptProtectionEnabled,
        int loginAttemptProtectionRequestLimitPerWindow,
        int loginAttemptProtectionRequestLimitWindowSeconds,
        bool passwordResetEnabled,
        int passwordResetRequestLimitPerWindow,
        int passwordResetRequestLimitWindowSeconds,
        bool emailVerificationEnabled,
        bool publicInvitationManagementEnabled,
        int publicInvitationManagementTokenTtlHours)
    {
        Validate(
            passwordRequiredLength,
            lockoutMaxFailedAccessAttempts,
            lockoutDefaultLockoutMinutes,
            loginRiskMode,
            loginRiskLowThreshold,
            loginRiskHighThreshold,
            loginRiskNewDeviceWeight,
            loginRiskNewIpWeight,
            loginRiskGeoShiftWeight,
            loginRiskFailureBurstWeight,
            loginRiskFailureBurstWindowMinutes,
            loginRiskFailureBurstThreshold,
            loginRiskHistoryWindowDays,
            loginRiskTrustedDeviceDays,
            interactiveLoginChallengeTtlSeconds,
            loginAttemptProtectionRequestLimitPerWindow,
            loginAttemptProtectionRequestLimitWindowSeconds,
            passwordResetRequestLimitPerWindow,
            passwordResetRequestLimitWindowSeconds,
            publicInvitationManagementTokenTtlHours);

        PasswordRequiredLength = passwordRequiredLength;
        PasswordRequireDigit = passwordRequireDigit;
        PasswordRequireLowercase = passwordRequireLowercase;
        PasswordRequireUppercase = passwordRequireUppercase;
        PasswordRequireNonAlphanumeric = passwordRequireNonAlphanumeric;
        LockoutAllowedForNewUsers = lockoutAllowedForNewUsers;
        LockoutMaxFailedAccessAttempts = lockoutMaxFailedAccessAttempts;
        LockoutDefaultLockoutMinutes = lockoutDefaultLockoutMinutes;
        LoginRiskEnabled = loginRiskEnabled;
        LoginRiskMode = loginRiskMode;
        LoginRiskLowThreshold = loginRiskLowThreshold;
        LoginRiskHighThreshold = loginRiskHighThreshold;
        LoginRiskNewDeviceWeight = loginRiskNewDeviceWeight;
        LoginRiskNewIpWeight = loginRiskNewIpWeight;
        LoginRiskGeoShiftWeight = loginRiskGeoShiftWeight;
        LoginRiskFailureBurstWeight = loginRiskFailureBurstWeight;
        LoginRiskFailureBurstWindowMinutes = loginRiskFailureBurstWindowMinutes;
        LoginRiskFailureBurstThreshold = loginRiskFailureBurstThreshold;
        LoginRiskHistoryWindowDays = loginRiskHistoryWindowDays;
        LoginRiskTrustedDeviceDays = loginRiskTrustedDeviceDays;
        InteractiveLoginChallengeTtlSeconds = interactiveLoginChallengeTtlSeconds;
        LoginAttemptProtectionEnabled = loginAttemptProtectionEnabled;
        LoginAttemptProtectionRequestLimitPerWindow = loginAttemptProtectionRequestLimitPerWindow;
        LoginAttemptProtectionRequestLimitWindowSeconds = loginAttemptProtectionRequestLimitWindowSeconds;
        PasswordResetEnabled = passwordResetEnabled;
        PasswordResetRequestLimitPerWindow = passwordResetRequestLimitPerWindow;
        PasswordResetRequestLimitWindowSeconds = passwordResetRequestLimitWindowSeconds;
        EmailVerificationEnabled = emailVerificationEnabled;
        PublicInvitationManagementEnabled = publicInvitationManagementEnabled;
        PublicInvitationManagementTokenTtlHours = publicInvitationManagementTokenTtlHours;
    }

    private static void Validate(
        int passwordRequiredLength,
        int lockoutMaxFailedAccessAttempts,
        int lockoutDefaultLockoutMinutes,
        LoginRiskMode loginRiskMode,
        int loginRiskLowThreshold,
        int loginRiskHighThreshold,
        int loginRiskNewDeviceWeight,
        int loginRiskNewIpWeight,
        int loginRiskGeoShiftWeight,
        int loginRiskFailureBurstWeight,
        int loginRiskFailureBurstWindowMinutes,
        int loginRiskFailureBurstThreshold,
        int loginRiskHistoryWindowDays,
        int loginRiskTrustedDeviceDays,
        int interactiveLoginChallengeTtlSeconds,
        int loginAttemptProtectionRequestLimitPerWindow,
        int loginAttemptProtectionRequestLimitWindowSeconds,
        int passwordResetRequestLimitPerWindow,
        int passwordResetRequestLimitWindowSeconds,
        int publicInvitationManagementTokenTtlHours)
    {
        if (passwordRequiredLength <= 0)
        {
            throw new SharedValidationException("Password required length must be positive.");
        }

        if (lockoutMaxFailedAccessAttempts < 3 || lockoutMaxFailedAccessAttempts > 20)
        {
            throw new SharedValidationException("Lockout max failed access attempts must be between 3 and 20.");
        }

        if (lockoutDefaultLockoutMinutes < 1 || lockoutDefaultLockoutMinutes > 1440)
        {
            throw new SharedValidationException("Lockout default lockout minutes must be between 1 and 1440.");
        }

        if (!Enum.IsDefined(loginRiskMode))
        {
            throw new SharedValidationException("Login risk mode is invalid.");
        }

        if (loginRiskLowThreshold < 0 || loginRiskLowThreshold >= loginRiskHighThreshold)
        {
            throw new SharedValidationException("Login risk thresholds are invalid.");
        }

        if (loginRiskNewDeviceWeight < 0 ||
            loginRiskNewIpWeight < 0 ||
            loginRiskGeoShiftWeight < 0 ||
            loginRiskFailureBurstWeight < 0)
        {
            throw new SharedValidationException("Login risk weights must be non-negative.");
        }

        if (loginRiskFailureBurstWindowMinutes <= 0)
        {
            throw new SharedValidationException("Login risk failure burst window must be positive.");
        }

        if (loginRiskFailureBurstThreshold <= 0)
        {
            throw new SharedValidationException("Login risk failure burst threshold must be positive.");
        }

        if (loginRiskHistoryWindowDays <= 0 || loginRiskTrustedDeviceDays <= 0)
        {
            throw new SharedValidationException("Login risk history and trusted device windows must be positive.");
        }

        if (interactiveLoginChallengeTtlSeconds < 30 || interactiveLoginChallengeTtlSeconds > 900)
        {
            throw new SharedValidationException("Interactive login challenge TTL must be between 30 and 900.");
        }

        if (loginAttemptProtectionRequestLimitPerWindow <= 0 || loginAttemptProtectionRequestLimitPerWindow > 500)
        {
            throw new SharedValidationException("Login attempt protection request limit must be between 1 and 500.");
        }

        if (loginAttemptProtectionRequestLimitWindowSeconds < 5 || loginAttemptProtectionRequestLimitWindowSeconds > 3600)
        {
            throw new SharedValidationException("Login attempt protection window must be between 5 and 3600 seconds.");
        }

        if (passwordResetRequestLimitPerWindow <= 0 || passwordResetRequestLimitPerWindow > 100)
        {
            throw new SharedValidationException("Password reset request limit must be between 1 and 100.");
        }

        if (passwordResetRequestLimitWindowSeconds < 30 || passwordResetRequestLimitWindowSeconds > 3600)
        {
            throw new SharedValidationException("Password reset request window must be between 30 and 3600 seconds.");
        }

        if (publicInvitationManagementTokenTtlHours < 1 || publicInvitationManagementTokenTtlHours > 720)
        {
            throw new SharedValidationException("Public invitation token TTL must be between 1 and 720 hours.");
        }
    }
}
