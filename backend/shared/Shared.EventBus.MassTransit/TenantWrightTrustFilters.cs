using System.Globalization;
using System.Text;
using MassTransit;
using MessageHeaders = MassTransit.SendHeaders;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.MassTransit;

/// <summary>
/// MassTransit send glue. Origin custody is never stamped here: every outbound message must already carry the origin
/// attestation the neutral outbox stamped (<see cref="TenantWrightStampedSendMarker"/>), or one of the MassTransit-owned
/// custody markers (delayed-redelivery relay, protected replay). The filter binds the broker identity to that custody
/// and requires mandatory routing with publisher confirms. Anything else fails closed.
/// </summary>
public sealed class TenantWrightSendTrustFilter<T>(
    TenantWrightMessageCatalog catalog,
    TenantWrightAttestationService attestations,
    Microsoft.Extensions.Options.IOptions<MessagingOptions> options,
    TimeProvider timeProvider) : IFilter<SendContext<T>> where T : class
{
    public async Task Send(SendContext<T> context, IPipe<SendContext<T>> next)
    {
        if (catalog.TryGetRoute(context.Message.GetType(), out var directRoute) &&
            directRoute.Kind == TenantWrightMessageRouteKind.Command)
            TenantWrightMessageContracts.EnsureAllowedOrigin(directRoute, attestations.Actor);

        if (TenantWrightProduceTrust.IsPublishHandoff(context) && !TenantWrightProduceTrust.HasCustodyMarker(context))
        {
            await next.Send(context);
            return;
        }

        // MassTransit can project one wire message through additional implemented-contract send filters. Custody is
        // applied to the concrete message before those projections; do not reinterpret an already-bound projection as
        // a new, unsupported contract.
        if (!TenantWrightProduceTrust.HasCustodyMarker(context) && TenantWrightProduceTrust.HasAppliedTransportTrust(context))
        {
            await next.Send(context);
            return;
        }

        if (!catalog.TryGetRoute(context.Message.GetType(), out _) &&
            TenantWrightProduceTrust.IsApprovedContractProjection(catalog, context) &&
            TenantWrightProduceTrust.HasOriginSignature(context))
        {
            await next.Send(context);
            return;
        }

        if (TenantWrightProduceTrust.TryApplyCustodyMarker(context))
        {
            await next.Send(context);
            return;
        }

        if (catalog.TryGetRoute(context.Message.GetType(), out var existingRoute) &&
            existingRoute.Kind == TenantWrightMessageRouteKind.Event &&
            TenantWrightProduceTrust.HasOriginSignature(context))
        {
            if (!TenantWrightProduceTrust.TryApplyRedeliveryCustody(
                    context, context.Message, catalog, attestations, options.Value, timeProvider))
                throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
            await next.Send(context);
            return;
        }

        TenantWrightProduceTrust.ApplyStamped(context, context.Message, TenantWrightMessageRouteKind.Command, catalog, attestations);
        await next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("tenantwrightSendTrust");
}

/// <summary>
/// MassTransit publish glue: requires the outbox-stamped origin custody of this host and binds the broker identity to
/// it. Never stamps a new origin.
/// </summary>
public sealed class TenantWrightPublishTrustFilter<T>(
    TenantWrightMessageCatalog catalog,
    TenantWrightAttestationService attestations) : IFilter<PublishContext<T>> where T : class
{
    public async Task Send(PublishContext<T> context, IPipe<PublishContext<T>> next)
    {
        TenantWrightMessageContracts.EnsureAllowedOrigin(catalog.GetRoute(context.Message.GetType()), attestations.Actor);
        TenantWrightProduceTrust.ApplyStamped(context, context.Message, TenantWrightMessageRouteKind.Event, catalog, attestations);
        context.GetOrAddPayload(static () => new TenantWrightPublishSendMarker());
        await next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("tenantwrightPublishTrust");
}

/// <summary>
/// The outbox-stamped custody a <see cref="MassTransitMessagingTransport"/> dispatch carries into the send pipeline,
/// bound to the exact message instance being dispatched.
/// </summary>
internal sealed record TenantWrightStampedSendMarker(
    object Message,
    OriginAttestation Origin,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Flows the stamped custody of one in-progress dispatch to the send and publish filters.
/// </summary>
/// <remarks>
/// MassTransit runs a caller-supplied send or publish pipe after the configured filters, so a payload added by the
/// caller is not visible to them. The ambient marker is scoped to the dispatch's own async flow and matched by
/// reference to the dispatched message, so neither a concurrent dispatch nor any other message MassTransit sends from
/// the same flow (faults, projections, redelivery) can borrow it.
/// </remarks>
internal static class TenantWrightStampedDispatch
{
    private static readonly AsyncLocal<TenantWrightStampedSendMarker?> Current = new();

    public static IDisposable Begin(TenantWrightStampedSendMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var previous = Current.Value;
        Current.Value = marker;
        return new Scope(previous);
    }

    public static bool TryGet(object message, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TenantWrightStampedSendMarker? marker)
    {
        var current = Current.Value;
        marker = current is not null && ReferenceEquals(current.Message, message) ? current : null;
        return marker is not null;
    }

    private sealed class Scope(TenantWrightStampedSendMarker? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Current.Value = previous;
        }
    }
}

internal static class TenantWrightProduceTrust
{
    public static bool IsPublishHandoff(SendContext context) =>
        context.TryGetPayload<TenantWrightPublishSendMarker>(out _);

    public static bool HasCustodyMarker(SendContext context) =>
        context.TryGetPayload<TenantWrightRelaySendMarker>(out _) ||
        context.TryGetPayload<TenantWrightReplaySendMarker>(out _);

    public static bool HasAppliedTransportTrust(SendContext context) =>
        context.Headers.TryGetHeader(TenantWrightTrustHeaders.OriginSignature, out var signature) &&
        signature is not null &&
        context.TryGetPayload<RabbitMqSendContext>(out var rabbit) &&
        rabbit.Mandatory &&
        rabbit.AwaitAck &&
        !string.IsNullOrWhiteSpace(rabbit.BasicProperties.UserId);

    /// <summary>
    /// Whether MassTransit is projecting an approved contract through one of its implemented-contract message types.
    /// </summary>
    public static bool IsApprovedContractProjection(TenantWrightMessageCatalog catalog, SendContext context) =>
        context.SupportedMessageTypes.Any(messageType =>
            catalog.Routes.Any(route => string.Equals(
                messageType,
                MessageUrn.ForTypeString(route.ContractType),
                StringComparison.Ordinal)));

    public static bool HasOriginSignature(SendContext context) =>
        context.Headers.TryGetHeader(TenantWrightTrustHeaders.OriginSignature, out var signature) &&
        signature is not null;

    public static bool TryApplyCustodyMarker(SendContext context)
    {
        if (context.TryGetPayload<TenantWrightRelaySendMarker>(out var relayMarker))
        {
            ApplyRelayCustody(context, relayMarker);
            return true;
        }

        if (context.TryGetPayload<TenantWrightReplaySendMarker>(out var replayMarker))
        {
            ApplyReplayCustody(context, replayMarker);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Binds a pre-stamped outbox message to the wire. The origin must be this host's own attestation for the exact
    /// contract being sent; the filter forwards it unchanged and never signs a new one.
    /// </summary>
    public static void ApplyStamped(
        SendContext context,
        object message,
        TenantWrightMessageRouteKind directKind,
        TenantWrightMessageCatalog catalog,
        TenantWrightAttestationService attestations)
    {
        if (!TenantWrightStampedDispatch.TryGet(message, out var marker))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedProducer);

        var runtimeType = message.GetType();
        var route = catalog.GetRoute(runtimeType);
        if (route.Kind != directKind ||
            !string.Equals(marker.Origin.ContractIdentity, TenantWrightMessageContracts.GetIdentity(runtimeType), StringComparison.Ordinal))
            throw new MessagingSecurityException(MessagingSecurityException.UnsupportedContract);
        if (!string.Equals(marker.Origin.OriginPrincipal, attestations.Actor, StringComparison.Ordinal))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedProducer);
        TenantWrightMessageContracts.EnsureAllowedOrigin(route, marker.Origin.OriginPrincipal);

        NeutralizeReservedHeaders(context);
        context.MessageId = marker.Origin.MessageId;
        WriteHeaders(context.Headers, marker.Headers);
        ApplyRabbitProperties(context, marker.Origin.OriginPrincipal);
    }

    public static bool TryApplyRedeliveryCustody(
        SendContext context,
        object message,
        TenantWrightMessageCatalog catalog,
        TenantWrightAttestationService attestations,
        MessagingOptions options,
        TimeProvider timeProvider)
    {
        if (!context.TryGetPayload<ConsumeContext>(out var consumeContext))
            return false;

        var destinationAddress = context.DestinationAddress;
        if (destinationAddress is null)
            return false;
        var delayExchange = destinationAddress.AbsolutePath.Trim('/').Split('/').Last();
        const string prefix = "mt-delay-";
        if (!delayExchange.StartsWith(prefix, StringComparison.Ordinal) ||
            !long.TryParse(delayExchange.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var delayMilliseconds))
            return false;

        var attempt = checked(consumeContext.GetRedeliveryCount() + 1);
        if (attempt < 1 || attempt > options.Delivery.DelayedRedeliverySeconds.Length ||
            delayMilliseconds != checked(options.Delivery.DelayedRedeliverySeconds[attempt - 1] * 1000L))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

        var runtimeType = message.GetType();
        var route = catalog.GetRoute(runtimeType);
        var endpointName = consumeContext.ReceiveContext.InputAddress.AbsolutePath.Trim('/').Split('/').Last();
        var endpoint = catalog.GetReceiveEndpoint(endpointName);
        if (route.Kind != TenantWrightMessageRouteKind.Event || !endpoint.ContractTypes.Contains(runtimeType) || !consumeContext.MessageId.HasValue)
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

        context.MessageId = consumeContext.MessageId;

        var payloadDigest = RequiredHeader(context, TenantWrightTrustHeaders.OriginPayloadSha256);
        var originSignature = RequiredHeader(context, TenantWrightTrustHeaders.OriginSignature);
        var origin = new OriginAttestation(
            consumeContext.MessageId.Value,
            TenantWrightMessageContracts.GetIdentity(runtimeType),
            RequiredHeader(context, TenantWrightTrustHeaders.Producer),
            RequiredHeader(context, TenantWrightTrustHeaders.Scope),
            context.Headers.Get<string>(TenantWrightTrustHeaders.TenantId),
            DateTimeOffset.ParseExact(
                RequiredHeader(context, TenantWrightTrustHeaders.OriginIssuedAt),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            payloadDigest,
            RequiredHeader(context, TenantWrightTrustHeaders.OriginKeyId),
            originSignature);
        var now = timeProvider.GetUtcNow();
        var notBefore = now + TimeSpan.FromMilliseconds(delayMilliseconds);
        var relay = attestations.CreateRelay(
            origin.MessageId,
            payloadDigest,
            TenantWrightAttestationService.SignatureDigest(originSignature),
            endpoint.Name,
            NewId.NextGuid().ToString("D", CultureInfo.InvariantCulture),
            attempt,
            notBefore,
            notBefore + options.Trust.MaximumRelayLifetime);
        ApplyRelayCustody(context, new TenantWrightRelaySendMarker(origin, relay));
        return true;
    }

    private static string RequiredHeader(SendContext context, string key) =>
        context.Headers.Get<string>(key)
        ?? throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);

    internal static void ApplyRelayCustody(SendContext context, TenantWrightRelaySendMarker marker)
    {
        NeutralizeReservedHeaders(context);
        context.MessageId = marker.Origin.MessageId;
        WriteHeaders(context.Headers, TenantWrightTrustHeaderMap.FromOrigin(marker.Origin));
        WriteHeaders(context.Headers, TenantWrightTrustHeaderMap.FromRelay(marker.Relay));
        ApplyRabbitProperties(context, marker.Relay.Actor);
    }

    internal static void ApplyReplayCustody(SendContext context, TenantWrightReplaySendMarker marker)
    {
        NeutralizeReservedHeaders(context);
        context.MessageId = marker.Origin.MessageId;
        WriteHeaders(context.Headers, TenantWrightTrustHeaderMap.FromOrigin(marker.Origin));
        if (marker.Relay is not null)
            WriteHeaders(context.Headers, TenantWrightTrustHeaderMap.FromRelay(marker.Relay));
        WriteHeaders(context.Headers, TenantWrightTrustHeaderMap.FromReplay(marker.Replay));
        ApplyRabbitProperties(context, marker.Replay.Actor);
    }

    private static void WriteHeaders(MessageHeaders target, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            if (!TenantWrightTrustHeaders.All.Contains(header.Key))
                throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);
            target.Set(header.Key, header.Value);
        }
    }

    private static void ApplyRabbitProperties(SendContext context, string actor)
    {
        if (!context.TryGetPayload<RabbitMqSendContext>(out var rabbit))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedProducer);

        rabbit.BasicProperties.UserId = actor;
        rabbit.Mandatory = true;
        rabbit.AwaitAck = true;
    }

    private static void NeutralizeReservedHeaders(SendContext context)
    {
        foreach (var header in TenantWrightTrustHeaders.All)
            context.Headers.Set(header, null);
    }
}

internal sealed class TenantWrightPublishSendMarker;

/// <summary>One RabbitMQ <c>x-death</c> entry: the queue that dead-lettered the delivery, why, how often and when.</summary>
public sealed record RabbitDeathRecord(string Queue, string Exchange, string Reason, long Count, DateTimeOffset? Time);

/// <summary>
/// Reads and verifies the RabbitMQ <c>x-death</c> history MassTransit's queue-based delayed redelivery leaves behind:
/// one expiry per configured <c>mt-delay-{milliseconds}</c> queue up to the signed relay attempt.
/// </summary>
internal static class RabbitDeathHistory
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Verify(
        IReadOnlyList<RabbitDeathRecord> records,
        IReadOnlyList<int> configuredIntervalsSeconds,
        int expectedAttempt,
        int maximumRecords,
        DateTimeOffset notBefore,
        DateTimeOffset now,
        TimeSpan maximumFutureClockSkew)
    {
        if (expectedAttempt < 1 || expectedAttempt > configuredIntervalsSeconds.Count ||
            records.Count != expectedAttempt || records.Count > maximumRecords)
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

        var expectedNames = configuredIntervalsSeconds.Take(expectedAttempt)
            .Select(seconds => $"mt-delay-{checked(seconds * 1000L)}")
            .ToHashSet(StringComparer.Ordinal);
        var currentDelayName = $"mt-delay-{checked(configuredIntervalsSeconds[expectedAttempt - 1] * 1000L)}";
        long totalCount = 0;
        foreach (var record in records)
        {
            if (!expectedNames.Remove(record.Queue) ||
                !string.Equals(record.Exchange, record.Queue, StringComparison.Ordinal) ||
                !string.Equals(record.Reason, "expired", StringComparison.Ordinal) ||
                record.Count < 1 || record.Count > expectedAttempt || record.Time is null ||
                record.Time.Value > now + maximumFutureClockSkew ||
                (string.Equals(record.Queue, currentDelayName, StringComparison.Ordinal) &&
                 record.Time.Value < notBefore - TimeSpan.FromSeconds(1)))
                throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
            totalCount = checked(totalCount + record.Count);
        }

        if (expectedNames.Count != 0 || totalCount != expectedAttempt)
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
    }

    public static IReadOnlyList<RabbitDeathRecord> Read(IDictionary<string, object?>? headers, int maximumRecords)
    {
        if (headers is null || !headers.TryGetValue("x-death", out var raw))
            return [];
        if (raw is string or byte[] || raw is not IEnumerable<object> entries)
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

        var result = new List<RabbitDeathRecord>();
        foreach (var entry in entries)
        {
            if (result.Count >= maximumRecords || entry is not IDictionary<string, object?> values)
                throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
            result.Add(new RabbitDeathRecord(
                Text(values, "queue"),
                Text(values, "exchange"),
                Text(values, "reason"),
                PositiveCount(values, "count"),
                Timestamp(values, "time")));
        }
        return result;
    }

    private static string Text(IDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
        var result = value switch
        {
            string text => text,
            byte[] bytes => StrictUtf8.GetString(bytes),
            ReadOnlyMemory<byte> bytes => StrictUtf8.GetString(bytes.Span),
            _ => throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody)
        };
        if (string.IsNullOrEmpty(result))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
        return result;
    }

    private static long PositiveCount(IDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
        var count = value switch
        {
            byte item => item,
            sbyte item when item > 0 => item,
            short item when item > 0 => item,
            ushort item => item,
            int item when item > 0 => item,
            uint item => item,
            long item when item > 0 => item,
            ulong item when item <= long.MaxValue => checked((long)item),
            _ => throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody)
        };
        return count > 0 ? count : throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
    }

    private static DateTimeOffset Timestamp(IDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
        try
        {
            return value switch
            {
                AmqpTimestamp timestamp => DateTimeOffset.FromUnixTimeSeconds(timestamp.UnixTime),
                DateTimeOffset timestamp when timestamp.Offset == TimeSpan.Zero => timestamp,
                DateTime timestamp when timestamp.Kind == DateTimeKind.Utc => new DateTimeOffset(timestamp),
                long timestamp => DateTimeOffset.FromUnixTimeSeconds(timestamp),
                _ => throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody)
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
        }
    }
}
