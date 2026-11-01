namespace Notifications.Domain.Shared.Enums;

[Flags]
public enum NotificationPolicyChannel
{
    None = 0,
    Email = 1,
    Sms = 2,
    Push = 4
}
