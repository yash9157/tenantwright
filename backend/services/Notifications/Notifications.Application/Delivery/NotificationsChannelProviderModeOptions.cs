namespace Notifications.Application.Delivery;

public sealed class NotificationsChannelProviderModeOptions
{
    public string Sms { get; set; } = "Disabled";
    public string Push { get; set; } = "Disabled";
    public string WhatsApp { get; set; } = "Disabled";
}
