namespace Notifications.Application.Contracts.Delivery;

public sealed class NotificationChannelDisabledByFeatureException : InvalidOperationException
{
    public NotificationChannelDisabledByFeatureException(
        string channel,
        string featureName)
        : base($"Notification channel '{channel}' is disabled by feature '{featureName}'.")
    {
        Channel = channel;
        FeatureName = featureName;
    }

    public string Channel { get; }

    public string FeatureName { get; }
}
