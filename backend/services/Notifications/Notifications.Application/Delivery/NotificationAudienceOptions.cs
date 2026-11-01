namespace Notifications.Application.Delivery;

public sealed class NotificationAudienceOptions
{
    /// <summary>
    /// Master switch for group recipient resolution. Disabled by default: the feature is a shared
    /// contract + cross-service change that is IN REVIEW, so it stays inert until explicitly enabled
    /// after sign-off. While disabled, a group recipient is rejected fail-closed.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Hard cap on the number of users a single group send may expand to. Exceeding it fails the schedule (fail-closed).</summary>
    public int MaxResolvedRecipients { get; set; } = 5000;
}
