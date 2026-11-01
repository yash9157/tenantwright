namespace Notifications.Application.Delivery;

public sealed class IdentityAudienceClientOptions
{
    public bool Enabled { get; set; } = true;
    public string? BaseAddress { get; set; }
    public string ClientProfile { get; set; } = "NotificationsInternal";
    public string Scope { get; set; } = "identity-internal";
    public int TimeoutSeconds { get; set; } = 5;
    public int PageSize { get; set; } = 200;
}
