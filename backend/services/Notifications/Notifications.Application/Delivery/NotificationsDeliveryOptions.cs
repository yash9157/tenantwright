namespace Notifications.Application.Delivery;

public sealed class NotificationsDeliveryOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalMs { get; set; } = 1000;
    public int BatchSize { get; set; } = 100;
    public int ProcessingLeaseSeconds { get; set; } = 60;
    public int MaxRetryCount { get; set; } = 5;
    public int BaseRetryDelaySeconds { get; set; } = 15;
    public int MaxRetryDelaySeconds { get; set; } = 300;
    public int OwnershipAcquireTimeoutMs { get; set; } = 100;
    public int OwnershipLeaseMs { get; set; } = 5000;
    public bool EnableSignalDrivenWorker { get; set; } = true;
    public string WakeupMode { get; set; } = "ValkeyPubSub";
    public string WakeupValkeyChannel { get; set; } = "tenantwright:notifications:delivery:wakeup";
    public int IdleSafetyPollMs { get; set; } = 30000;
    public int MaxScheduleHorizonDays { get; set; } = 365;
}
