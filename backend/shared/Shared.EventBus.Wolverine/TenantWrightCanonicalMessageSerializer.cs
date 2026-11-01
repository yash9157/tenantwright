using System.Text.Json;
using Shared.EventBus.Messaging;
using Wolverine;
using Wolverine.Runtime.Serialization;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// Deserializes a delivery into exactly one of its receive endpoint's approved contracts with the catalog's
/// owner-generated canonical metadata, so the object the verifier digests reproduces the producer's canonical payload.
/// </summary>
/// <remarks>
/// Any other content type, any contract outside the endpoint and any malformed payload throws, which Wolverine treats
/// as a deserialization failure and dead-letters without retrying. Serialization is unsupported: a TenantWright receive
/// endpoint never sends through Wolverine.
/// </remarks>
internal sealed class TenantWrightCanonicalMessageSerializer(TenantWrightMessageCatalog catalog, TenantWrightReceiveEndpoint endpoint)
    : IMessageSerializer
{
    public const string JsonContentType = "application/json";

    public string ContentType => JsonContentType;

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(envelope.ContentType, JsonContentType, StringComparison.Ordinal))
            throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);
        if (!endpoint.ContractTypes.Contains(messageType))
            throw new MessagingSecurityException(MessagingSecurityException.UnsupportedContract);

        try
        {
            return catalog.DeserializeCanonical(envelope.Body.Span, messageType);
        }
        catch (JsonException)
        {
            throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);
        }
    }

    public object ReadFromData(byte[] data) => throw Unsupported();

    public byte[] Write(Envelope envelope) => throw Unsupported();

    public byte[] WriteMessage(object message) => throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new("TenantWright receive endpoints only deserialize approved contracts of their own catalog endpoint.");
}
