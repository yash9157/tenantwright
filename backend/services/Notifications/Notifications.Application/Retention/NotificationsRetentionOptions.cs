namespace Notifications.Application.Retention;

/// <summary>Hot-storage archive and ephemeral presence retention settings.</summary>
public sealed class NotificationsRetentionOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 3600;

    /// <summary>Completed notification aggregates remain queryable in hot storage for 12 months.</summary>
    public int HotRetentionDays { get; set; } = 365;

    /// <summary>Presence observations are ephemeral and retained for 90 days.</summary>
    public int PresenceRetentionDays { get; set; } = 90;

    public int BatchSize { get; set; } = 500;
}
