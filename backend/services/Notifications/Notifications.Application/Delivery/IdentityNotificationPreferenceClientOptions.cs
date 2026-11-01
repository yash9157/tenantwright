namespace Notifications.Application.Delivery;

public sealed class IdentityNotificationPreferenceClientOptions
{
    public bool Enabled { get; set; } = true;
    public bool FailClosed { get; set; } = true;
    public string? BaseAddress { get; set; }

    /// <summary>
    /// The <c>OpenIddict:Clients:{profile}</c> section whose client-credentials identity calls Identity. Notifications
    /// owns exactly one internal identity (<c>tenantwright-notifications-internal</c>), shared with the audience client.
    /// </summary>
    public string ClientProfile { get; set; } = "NotificationsInternal";

    public string Scope { get; set; } = "identity-internal";
    public int TimeoutSeconds { get; set; } = 3;
}
