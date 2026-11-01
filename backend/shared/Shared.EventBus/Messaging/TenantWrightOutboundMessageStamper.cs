using Shared.MultiTenancy;

namespace Shared.EventBus.Messaging;

/// <summary>
/// A distributed message after outbound trust stamping: the canonical payload and the signed origin custody that a
/// durable outbox persists and a transport later carries unchanged.
/// </summary>
/// <param name="TenantId">Operational tenant reference only; the signed origin header is the trust source.</param>
public sealed record TenantWrightOutboundMessage(
    Guid MessageId,
    Type ContractType,
    string ContractIdentity,
    TenantWrightMessageRouteKind Kind,
    string? Destination,
    byte[] Payload,
    IReadOnlyDictionary<string, string> Headers,
    Guid? TenantId,
    OriginAttestation Origin);

/// <summary>
/// Transport-neutral outbound trust stamping. Resolves the reviewed route, enforces the producer allow-list and the
/// host-only/tenant-required tenancy rules, serializes the canonical payload and signs the origin attestation.
/// </summary>
/// <remarks>
/// Stamping happens once, at the point the producer records the message, under the producer's ambient tenant. A
/// transport never re-stamps: it forwards these headers and binds the broker identity to their producer.
/// </remarks>
public sealed class TenantWrightOutboundMessageStamper(
    TenantWrightMessageCatalog catalog,
    TenantWrightAttestationService attestations,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider)
{
    public TenantWrightOutboundMessage Stamp(object message, TenantWrightMessageRouteKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(message);

        var runtimeType = message.GetType();
        var route = catalog.GetRoute(runtimeType);
        if (route.Kind != expectedKind)
            throw new MessagingSecurityException(MessagingSecurityException.UnsupportedContract);
        TenantWrightMessageContracts.EnsureAllowedOrigin(route, attestations.Actor);

        var metadata = TenantWrightMessageContracts.GetMetadata(runtimeType);
        var tenantId = TenantWrightMessageContracts.ResolveTenant(metadata, message, currentTenant);
        var identity = TenantWrightMessageContracts.GetIdentity(runtimeType);
        var payload = catalog.SerializeCanonical(message, runtimeType);
        var issuedAt = timeProvider.GetUtcNow();
        var messageId = Guid.CreateVersion7(issuedAt);
        var origin = attestations.CreateOrigin(
            messageId,
            identity,
            TenantWrightMessageContracts.ScopeOf(metadata),
            tenantId,
            issuedAt,
            TenantWrightAttestationService.Sha256(payload));

        return new TenantWrightOutboundMessage(
            messageId,
            runtimeType,
            identity,
            route.Kind,
            route.Kind == TenantWrightMessageRouteKind.Command ? route.CommandDestination : null,
            payload,
            TenantWrightTrustHeaderMap.FromOrigin(origin),
            tenantId is null ? null : Guid.ParseExact(tenantId, "D"),
            origin);
    }
}
