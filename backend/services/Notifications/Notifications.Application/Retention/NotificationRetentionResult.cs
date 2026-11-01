namespace Notifications.Application.Retention;

public sealed record NotificationRetentionResult(
    int ArchivedNotifications,
    int PurgedPresenceRecords)
{
    public int Total => ArchivedNotifications + PurgedPresenceRecords;

    public static NotificationRetentionResult Empty { get; } = new(0, 0);
}
