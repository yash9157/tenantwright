using System.Text;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;
using Wolverine;
using Wolverine.RabbitMQ.Internal;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// The broker facts of one RabbitMQ delivery, captured by <see cref="TenantWrightRabbitMqEnvelopeMapper"/> from the AMQP
/// properties before any TenantWright code runs, and carried to the handler on <see cref="Envelope.RoutingInformation"/>,
/// which Wolverine never reads from or writes to the wire.
/// </summary>
/// <param name="Endpoint">The catalog receive endpoint whose queue the broker delivered from.</param>
/// <param name="MessageId">The AMQP <c>message-id</c>, when it is an exact GUID.</param>
/// <param name="BrokerActor">The AMQP <c>user-id</c>, which RabbitMQ only accepts when it equals the publisher's
/// authenticated user.</param>
/// <param name="TrustHeaders">Every reserved trust header present in the AMQP headers; an unreadable value is present
/// with a <see langword="null"/> value.</param>
internal sealed record TenantWrightRabbitMqDelivery(
    string Endpoint,
    Guid? MessageId,
    string? BrokerActor,
    IReadOnlyDictionary<string, string?> TrustHeaders,
    TenantWrightTransportEvidence TransportEvidence);

/// <summary>
/// Evidence that RabbitMQ dead-lettered this delivery at least once (an <c>x-death</c> header). The Wolverine provider
/// performs no broker redelivery of its own, so such a delivery can only be a dead-lettered message moved back into a
/// receive queue; with no redelivery custody verifier registered, the neutral verifier rejects it.
/// </summary>
internal sealed class RabbitMqDeadLetterHistoryEvidence : TenantWrightTransportEvidence
{
    public static RabbitMqDeadLetterHistoryEvidence Instance { get; } = new();

    private RabbitMqDeadLetterHistoryEvidence()
    {
    }

    public override bool HasRedeliveryHistory => true;
}

/// <summary>
/// The incoming-only envelope mapper of one receive endpoint. It maps exactly what the TenantWright pipeline needs and
/// nothing Wolverine would otherwise act on: no reply, acknowledgement, scheduling, saga, tenant or correlation header
/// is ever read from the wire, so a sender cannot steer Wolverine through envelope metadata.
/// </summary>
/// <remarks>
/// Mapping never throws: Wolverine settles a delivery whose mapping fails outside its failure policy. Anything
/// malformed is carried forward instead and rejected by <see cref="TenantWrightCanonicalMessageSerializer"/> or the trust
/// verifier, both of which dead-letter through the endpoint's failure policy.
/// </remarks>
internal sealed class TenantWrightRabbitMqEnvelopeMapper : IRabbitMqEnvelopeMapper
{
    private const string DeathHeader = "x-death";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly TenantWrightReceiveEndpoint _endpoint;
    private readonly HashSet<string> _contractIdentities;
    private readonly TenantWrightCanonicalMessageSerializer _serializer;

    public TenantWrightRabbitMqEnvelopeMapper(TenantWrightReceiveEndpoint endpoint, TenantWrightMessageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(catalog);
        _endpoint = endpoint;
        _contractIdentities = endpoint.ContractTypes
            .Select(TenantWrightMessageContracts.GetIdentity)
            .ToHashSet(StringComparer.Ordinal);
        _serializer = new TenantWrightCanonicalMessageSerializer(catalog, endpoint);
    }

    public void MapIncomingToEnvelope(Envelope envelope, IReadOnlyBasicProperties incoming)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(incoming);

        var messageId = Guid.TryParseExact(incoming.MessageId, "D", out var parsed) ? parsed : (Guid?)null;
        var headers = incoming.Headers;

        // Wolverine requires an envelope id; a delivery without an exact GUID message id is rejected by the verifier.
        envelope.Id = messageId ?? Guid.NewGuid();
        envelope.ContentType = incoming.ContentType;
        envelope.MessageType = incoming.Type is { } type && _contractIdentities.Contains(type) ? type : null;
        envelope.Serializer = _serializer;
        envelope.RoutingInformation = new TenantWrightRabbitMqDelivery(
            _endpoint.Name,
            messageId,
            incoming.UserId,
            ReadTrustHeaders(headers),
            headers?.ContainsKey(DeathHeader) == true
                ? RabbitMqDeadLetterHistoryEvidence.Instance
                : NoTenantWrightTransportEvidence.Instance);
    }

    public void MapEnvelopeToOutgoing(Envelope envelope, IBasicProperties outgoing) =>
        throw new NotSupportedException(
            "TenantWright receive endpoints never send through Wolverine; outbound dispatch uses the confirmed publisher.");

    internal static Dictionary<string, string?> ReadTrustHeaders(IDictionary<string, object?>? headers)
    {
        var trust = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (headers is null)
            return trust;

        foreach (var name in TenantWrightTrustHeaders.All)
        {
            if (headers.TryGetValue(name, out var raw))
                trust[name] = Text(raw);
        }

        return trust;
    }

    private static string? Text(object? raw)
    {
        try
        {
            return raw switch
            {
                byte[] bytes => StrictUtf8.GetString(bytes),
                ReadOnlyMemory<byte> bytes => StrictUtf8.GetString(bytes.Span),
                string text => text,
                _ => null
            };
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
