namespace Notifications.Application.Delivery;

public sealed class NotificationsChannelsOptions
{
    public bool EmailEnabled { get; set; } = true;
    public bool AppEnabled { get; set; } = true;
    public bool SmsEnabled { get; set; }
    public bool PushEnabled { get; set; }
    public bool WhatsAppEnabled { get; set; }
}
