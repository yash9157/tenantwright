using System.Security.Cryptography;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// The Wolverine provider's <see cref="ITenantWrightMessagingTransport"/>. Rehydrates the canonical outbox payload into its
/// exact contract, proves the re-serialized canonical bytes still match the signed origin digest, and publishes those
/// bytes with the outbox message ID and the pre-stamped origin headers through <see cref="RabbitMqConfirmedPublisher"/>.
/// </summary>
/// <remarks>
/// The AMQP <c>user-id</c> is the signed origin principal, which is this host's producer principal and its broker user;
/// RabbitMQ refuses a <c>user-id</c> that differs from the authenticated user, so the broker identity the consumer reads
/// is bound to the signed origin. A command goes to the exchange named by its destination and an event to the exchange
/// named by its contract identity; the producer never declares either, nor any consumer queue. An absent exchange or
/// an exchange with no binding fails the dispatch, and the outbox retries it.
/// </remarks>
internal sealed class WolverineMessagingTransport(
    RabbitMqConfirmedPublisher publisher,
    TenantWrightMessageCatalog catalog,
    TenantWrightAttestationService attestations,
    TimeProvider timeProvider) : ITenantWrightMessagingTransport
{
    public async Task DispatchAsync(TenantWrightTransportMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var route = catalog.GetRouteByIdentity(message.ContractIdentity);
        if (route.Kind != message.Kind)
            throw new MessagingSecurityException(MessagingSecurityException.UnsupportedContract);
        if (route.Kind == TenantWrightMessageRouteKind.Command
                ? !string.Equals(route.CommandDestination, message.Destination, StringComparison.Ordinal)
                : message.Destination is not null)
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

        var headers = message.Headers.ToDictionary(
            header => header.Key, string? (header) => header.Value, StringComparer.Ordinal);
        var origin = TenantWrightTrustHeaderMap.ReadOrigin(headers, message.MessageId, message.ContractIdentity);
        if (!string.Equals(origin.OriginPrincipal, attestations.Actor, StringComparison.Ordinal))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedProducer);
        TenantWrightMessageContracts.EnsureAllowedOrigin(route, origin.OriginPrincipal);

        var contract = catalog.DeserializeCanonical(message.Payload.Span, route.ContractType);
        var canonical = catalog.SerializeCanonical(contract, route.ContractType);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(TenantWrightAttestationService.Sha256(canonical)),
                Convert.FromHexString(origin.PayloadSha256)))
            throw new MessagingSecurityException(MessagingSecurityException.InvalidSignature);

        await publisher.PublishAsync(
            TenantWrightWolverineTopology.ExchangeFor(route),
            CreateProperties(message, origin.OriginPrincipal, timeProvider.GetUtcNow()),
            canonical,
            cancellationToken);
    }

    /// <summary>
    /// The AMQP properties of one dispatch: the outbox message ID, the signed origin principal as <c>user-id</c>, the
    /// contract identity as <c>type</c>, persistent JSON, and the stamped origin headers. A header that is not a
    /// reserved trust header fails closed.
    /// </summary>
    internal static BasicProperties CreateProperties(
        TenantWrightTransportMessage message,
        string originPrincipal,
        DateTimeOffset now)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var header in message.Headers)
        {
            if (!TenantWrightTrustHeaders.All.Contains(header.Key))
                throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);
            headers[header.Key] = header.Value;
        }

        return new BasicProperties
        {
            MessageId = message.MessageId.ToString("D"),
            UserId = originPrincipal,
            Type = message.ContractIdentity,
            ContentType = TenantWrightCanonicalMessageSerializer.JsonContentType,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(now.ToUnixTimeSeconds()),
            Headers = headers
        };
    }
}
