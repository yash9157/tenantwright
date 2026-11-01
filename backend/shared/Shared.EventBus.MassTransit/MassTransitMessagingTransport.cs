using System.Security.Cryptography;
using MassTransit;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.MassTransit;

/// <summary>
/// The MassTransit/RabbitMQ implementation of <see cref="ITenantWrightMessagingTransport"/>. Rehydrates the canonical
/// outbox payload into its exact contract, then sends a command to its declared destination exchange or publishes an
/// event with the outbox message ID and the pre-stamped origin headers. The send filters bind the broker identity to
/// that origin and require mandatory routing with publisher confirms, so the awaited call completes only after the
/// broker accepted and routed the message.
/// </summary>
/// <remarks>
/// Commands are sent to <c>exchange:{destination}</c> rather than <c>queue:{destination}</c>. A producer must not
/// declare a consumer's queue: MassTransit would create it with producer-side defaults (a classic queue) and the
/// owner's quorum declaration would then fail. Mandatory routing makes an unbound destination a dispatch failure that
/// the outbox retries, which is the fail-closed outcome.
/// </remarks>
public sealed class MassTransitMessagingTransport(
    IBus bus,
    TenantWrightMessageCatalog catalog,
    TenantWrightAttestationService attestations) : ITenantWrightMessagingTransport
{
    public async Task DispatchAsync(TenantWrightTransportMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var route = catalog.GetRouteByIdentity(message.ContractIdentity);
        if (route.Kind != message.Kind)
            throw new MessagingSecurityException(MessagingSecurityException.UnsupportedContract);

        var headers = message.Headers.ToDictionary(
            header => header.Key, string? (header) => header.Value, StringComparer.Ordinal);
        var origin = TenantWrightTrustHeaderMap.ReadOrigin(headers, message.MessageId, message.ContractIdentity);
        if (!string.Equals(origin.OriginPrincipal, attestations.Actor, StringComparison.Ordinal))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedProducer);

        var contract = catalog.DeserializeCanonical(message.Payload.Span, route.ContractType);
        var digest = TenantWrightAttestationService.Sha256(catalog.SerializeCanonical(contract, route.ContractType));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(digest),
                Convert.FromHexString(origin.PayloadSha256)))
            throw new MessagingSecurityException(MessagingSecurityException.InvalidSignature);

        using var dispatch = TenantWrightStampedDispatch.Begin(new TenantWrightStampedSendMarker(contract, origin, message.Headers));
        if (route.Kind == TenantWrightMessageRouteKind.Command)
        {
            if (route.CommandDestination is null ||
                !string.Equals(route.CommandDestination, message.Destination, StringComparison.Ordinal))
                throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
            var endpoint = await bus.GetSendEndpoint(new Uri($"exchange:{route.CommandDestination}"));
            await endpoint.Send(contract, route.ContractType, cancellationToken);
            return;
        }

        if (message.Destination is not null)
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
        await bus.Publish(contract, route.ContractType, cancellationToken);
    }
}
