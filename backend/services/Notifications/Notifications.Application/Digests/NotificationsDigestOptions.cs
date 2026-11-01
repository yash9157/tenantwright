namespace Notifications.Application.Digests;

public sealed class NotificationsDigestOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 100;
}
