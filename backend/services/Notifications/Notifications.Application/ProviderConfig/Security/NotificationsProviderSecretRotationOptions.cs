namespace Notifications.Application.ProviderConfig.Security;

public sealed class NotificationsProviderSecretRotationOptions
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 300;
    public int BatchSize { get; set; } = 100;
}
