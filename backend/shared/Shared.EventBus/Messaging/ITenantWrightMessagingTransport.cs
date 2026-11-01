namespace Shared.EventBus.Messaging;

/// <summary>A stamped message handed from the durable outbox to the active transport provider.</summary>
/// <param name="Payload">The canonical JSON payload the origin attestation digests.</param>
/// <param name="Headers">The pre-stamped origin trust headers. The transport forwards them unchanged.</param>
public sealed record TenantWrightTransportMessage(
    Guid MessageId,
    string ContractIdentity,
    TenantWrightMessageRouteKind Kind,
    string? Destination,
    ReadOnlyMemory<byte> Payload,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// The provider seam between the neutral outbox dispatcher and a broker. One provider is active per deployment.
/// </summary>
public interface ITenantWrightMessagingTransport
{
    /// <summary>
    /// Sends a command to its destination or publishes an event, carrying <see cref="TenantWrightTransportMessage.MessageId"/>
    /// and every header unchanged. Completes only after the broker has confirmed it accepted and routed the message;
    /// any doubt must surface as an exception so the outbox retries.
    /// </summary>
    Task DispatchAsync(TenantWrightTransportMessage message, CancellationToken cancellationToken);
}
