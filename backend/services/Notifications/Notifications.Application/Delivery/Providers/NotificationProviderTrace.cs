using System.Diagnostics;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Delivery.Providers;

internal static class NotificationProviderTrace
{
    private static readonly ActivitySource Source = new("Notifications.Delivery");

    public static Activity Start(NotificationChannel channel)
    {
        // Keep the privacy scope even when sampling drops the exported span. Child HTTP instrumentation
        // must never capture a provider URL containing a device token or subscription capability.
        var activity = Source.StartActivity("notification.provider.send", ActivityKind.Client)
            ?? new Activity("notification.provider.send").Start();
        activity.SetTag("telemetry.suppress_http_instrumentation", true);
        activity.SetTag("notification.channel", channel switch
        {
            NotificationChannel.Email => "email", NotificationChannel.Sms => "sms", NotificationChannel.Push => "push",
            NotificationChannel.WhatsApp => "whatsapp", NotificationChannel.App => "app", _ => "invalid"
        });
        return activity;
    }
}
