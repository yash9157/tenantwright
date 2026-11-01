namespace Notifications.Domain.Shared.Enums;

public enum NotificationProviderVerificationState
{
    Configured = 1,
    TestSendPassed = 2,
    LiveVerified = 3,
    VerificationFailed = 4
}
