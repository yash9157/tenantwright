using Shared.BackgroundTasks.Workers;

namespace Notifications.Application.Delivery;

public static class NotificationDeliveryWakeKeys
{
    public const string Delivery = "notifications:delivery";

    public static BackgroundWorkerWakeOptions CreateWakeOptions(NotificationsDeliveryOptions options)
    {
        if (!Enum.TryParse<BackgroundWorkerWakeMode>(options.WakeupMode, ignoreCase: true, out var wakeupMode))
        {
            throw new InvalidOperationException("Notifications:Delivery:WakeupMode must be 'LocalOnly' or 'ValkeyPubSub'.");
        }

        return new BackgroundWorkerWakeOptions
        {
            Enabled = options.EnableSignalDrivenWorker,
            WakeupMode = wakeupMode,
            ValkeyChannel = string.IsNullOrWhiteSpace(options.WakeupValkeyChannel)
                ? "tenantwright:notifications:delivery:wakeup"
                : options.WakeupValkeyChannel.Trim(),
            SafetyPollTimeout = TimeSpan.FromMilliseconds(Math.Clamp(options.IdleSafetyPollMs, 1000, 300000))
        };
    }
}
