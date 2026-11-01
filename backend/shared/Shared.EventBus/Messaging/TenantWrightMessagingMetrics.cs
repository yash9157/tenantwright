using System.Diagnostics.Metrics;

namespace Shared.EventBus.Messaging;

/// <summary>
/// Provider-neutral delivery telemetry. Tags carry only contract identities, endpoint names and fixed reason codes:
/// never payloads, tenant identifiers or exception messages.
/// </summary>
public static class TenantWrightMessagingMetrics
{
    public const string OutboxMeterName = "Shared.EventBus.Outbox";
    public const string InboxMeterName = "Shared.EventBus.Inbox";
    public const string SenderMeterName = "Shared.EventBus.Sender";

    private static readonly Meter OutboxMeter = new(OutboxMeterName);
    private static readonly Meter InboxMeter = new(InboxMeterName);
    private static readonly Meter SenderMeter = new(SenderMeterName);

    internal static readonly Counter<long> EventDropped =
        SenderMeter.CreateCounter<long>("tenantwright.messaging.event.dropped",
            description: "Events a bus-free host (Messaging:Provider=None) dropped instead of publishing.");

    internal static readonly Counter<long> OutboxDispatched =
        OutboxMeter.CreateCounter<long>("tenantwright.messaging.outbox.dispatched");

    internal static readonly Counter<long> OutboxFailed =
        OutboxMeter.CreateCounter<long>("tenantwright.messaging.outbox.failed");

    internal static readonly Counter<long> OutboxParked =
        OutboxMeter.CreateCounter<long>("tenantwright.messaging.outbox.parked");

    internal static readonly Histogram<double> OutboxLag =
        OutboxMeter.CreateHistogram<double>("tenantwright.messaging.outbox.lag", unit: "ms",
            description: "Time from outbox write to broker-confirmed dispatch.");

    internal static readonly Counter<long> InboxProcessed =
        InboxMeter.CreateCounter<long>("tenantwright.messaging.inbox.processed");

    internal static readonly Counter<long> InboxDuplicate =
        InboxMeter.CreateCounter<long>("tenantwright.messaging.inbox.duplicate");

    internal static readonly Counter<long> InboxRejected =
        InboxMeter.CreateCounter<long>("tenantwright.messaging.inbox.rejected");

    public static void RecordDispatched(string contractIdentity, TimeSpan lag)
    {
        var tag = new KeyValuePair<string, object?>("contract", contractIdentity);
        OutboxDispatched.Add(1, tag);
        OutboxLag.Record(Math.Max(0, lag.TotalMilliseconds), tag);
    }

    public static void RecordDispatchFailed(string contractIdentity, string errorType) =>
        OutboxFailed.Add(1,
            new KeyValuePair<string, object?>("contract", contractIdentity),
            new KeyValuePair<string, object?>("error_type", errorType));

    internal static void RecordEventDropped(string contractIdentity) =>
        EventDropped.Add(1, new KeyValuePair<string, object?>("contract", contractIdentity));

    public static void RecordParked(string contractIdentity, string reason) =>
        OutboxParked.Add(1,
            new KeyValuePair<string, object?>("contract", contractIdentity),
            new KeyValuePair<string, object?>("reason", reason));
}
