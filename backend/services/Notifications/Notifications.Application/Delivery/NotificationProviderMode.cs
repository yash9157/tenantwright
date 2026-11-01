namespace Notifications.Application.Delivery;

internal enum NotificationProviderMode
{
    Disabled = 0,
    MockSuccess = 1,
    Live = 2
}

internal static class NotificationProviderModeParser
{
    public static NotificationProviderMode Parse(string? value)
    {
        return Enum.TryParse<NotificationProviderMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : NotificationProviderMode.Disabled;
    }
}
