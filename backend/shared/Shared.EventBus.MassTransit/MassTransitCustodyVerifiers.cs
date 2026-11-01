using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.MassTransit;

/// <summary>The RabbitMQ basic properties of one MassTransit delivery, including any broker death history.</summary>
internal sealed class RabbitMqTransportEvidence(IReadOnlyBasicProperties properties) : TenantWrightTransportEvidence
{
    public IDictionary<string, object?>? RawHeaders => properties.Headers;

    public override bool HasRedeliveryHistory => properties.Headers?.ContainsKey("x-death") == true;
}

/// <summary>
/// MassTransit's queue-based delayed redelivery custody: the broker's <c>x-death</c> history must prove exactly the
/// configured <c>mt-delay-*</c> expirations the signed relay attempt claims.
/// </summary>
internal sealed class MassTransitRedeliveryCustodyVerifier(IOptions<MessagingOptions> options)
    : ITenantWrightRedeliveryCustodyVerifier
{
    public void VerifyRedeliveryHistory(TenantWrightInboundMessage message, RelayAttestation relay, DateTimeOffset now)
    {
        if (message.TransportEvidence is not RabbitMqTransportEvidence evidence)
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

        var value = options.Value;
        var deaths = RabbitDeathHistory.Read(evidence.RawHeaders, value.Replay.MaximumDeathRecords);
        RabbitDeathHistory.Verify(
            deaths,
            value.Delivery.DelayedRedeliverySeconds,
            relay.Attempt,
            value.Replay.MaximumDeathRecords,
            relay.NotBefore,
            now,
            value.Trust.MaximumFutureClockSkew);
    }
}

/// <summary>
/// MassTransit's protected replay custody: a replay arrives as a fresh RabbitMQ publication by the replay principal,
/// never as a broker redelivery.
/// </summary>
internal sealed class MassTransitReplayCustodyVerifier : ITenantWrightReplayCustodyVerifier
{
    public void VerifyReplayDelivery(TenantWrightInboundMessage message, ReplayAttestation replay, DateTimeOffset now)
    {
        if (message.TransportEvidence is not RabbitMqTransportEvidence evidence || evidence.HasRedeliveryHistory)
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);
    }
}
