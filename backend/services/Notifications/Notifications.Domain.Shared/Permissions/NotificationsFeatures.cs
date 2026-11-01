namespace Shared.Permissions.Definitions.Notifications;

public static class NotificationsFeatures
{
    public const string GroupName = "Notifications";

    public const string Email = "Notifications.Email";
    public const string Sms = "Notifications.Sms";
    public const string Push = "Notifications.Push";
    public const string WhatsApp = "Notifications.WhatsApp";
    public const string Realtime = "Notifications.Realtime";

    /// <summary>Single-choice fallback delivery channel (a <c>Selection</c> feature).</summary>
    public const string DefaultChannel = "Notifications.DefaultChannel";

    // Per-channel monthly allowances (bounded <c>Number</c> features). Each channel carries its own
    // cap, range, and unit — the naming keeps every cap adjacent to its channel toggle in the
    // alphabetically-ordered feature list.
    public const string EmailMonthlyCap = "Notifications.EmailMonthlyCap";
    public const string SmsMonthlyCap = "Notifications.SmsMonthlyCap";
    public const string PushMonthlyCap = "Notifications.PushMonthlyCap";
    public const string WhatsAppMonthlyCap = "Notifications.WhatsAppMonthlyCap";

    public const string ChannelEmail = "Email";
    public const string ChannelSms = "SMS";
    public const string ChannelPush = "Push";
}
