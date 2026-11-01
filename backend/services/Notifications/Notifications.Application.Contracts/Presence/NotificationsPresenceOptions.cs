namespace Notifications.Application.Contracts.Presence;

public class NotificationsPresenceOptions
{
    public bool Enabled { get; set; } = true;
    public int TtlSeconds { get; set; } = 60;
}
