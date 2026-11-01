namespace Notifications.Realtime;

public class NotificationsRealtimeOptions
{
    public bool Enabled { get; set; } = true;

    public NotificationsRealtimeValkeyOptions Valkey { get; set; } = new();
}

public class NotificationsRealtimeValkeyOptions
{
    public string? ChannelPrefix { get; set; }
}
