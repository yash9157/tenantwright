using System.Diagnostics.Metrics;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Dispatches;

internal static class NotificationDispatchTelemetry
{
    private static readonly Meter Meter = new("Notifications.Dispatch");
    private static readonly Counter<long> Admitted = Meter.CreateCounter<long>("notifications.dispatch.admitted");
    private static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("notifications.dispatch.duplicates");
    private static readonly Counter<long> Evaluated = Meter.CreateCounter<long>("notifications.dispatch.steps.evaluated");

    public static void Admission(NotificationDispatchMode mode, bool duplicate) =>
        (duplicate ? Duplicates : Admitted).Add(1, new KeyValuePair<string, object?>("mode", Mode(mode)));

    public static void Evaluation(NotificationDispatchMode mode, NotificationChannel channel) => Evaluated.Add(1,
        new KeyValuePair<string, object?>("mode", Mode(mode)), new KeyValuePair<string, object?>("channel", channel switch
        {
            NotificationChannel.Email => "email", NotificationChannel.Sms => "sms", NotificationChannel.Push => "push",
            NotificationChannel.App => "app", NotificationChannel.WhatsApp => "whatsapp", _ => "invalid"
        }));

    private static string Mode(NotificationDispatchMode mode) => mode switch
    {
        NotificationDispatchMode.Parallel => "parallel", NotificationDispatchMode.OrderedFallback => "fallback", _ => "invalid"
    };
}
