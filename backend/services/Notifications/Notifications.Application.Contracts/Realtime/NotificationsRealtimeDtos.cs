namespace Notifications.Application.Contracts.Realtime;

public class AppNotificationSignalDto
{
    public Guid NotificationId { get; set; }
    public Guid? TenantId { get; set; }
    public string UserId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string? Subject { get; set; }
    public DateTime CreationTimeUtc { get; set; }
}

public class SessionRevokedSignalDto
{
    public Guid? TenantId { get; set; }
    public string UserId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Action { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; }
}
