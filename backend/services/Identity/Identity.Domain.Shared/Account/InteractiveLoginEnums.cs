namespace Identity.Domain.Shared.Account;

public enum InteractiveLoginStep
{
    Failed = 0,
    Succeeded = 1,
    RequiresMfa = 2,
    RequiresTrustDevice = 3,
    PasswordExpired = 4,
    Locked = 5,
    Blocked = 6,
    RequiresMfaEnrollment = 7
}

public enum InteractiveLoginMode
{
    Tenant = 0,
    Platform = 1
}

public enum PasswordResetValidationStatus
{
    Invalid = 0,
    Valid = 1
}
