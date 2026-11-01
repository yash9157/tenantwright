using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Shared.EventBus.Messaging;

/// <summary>
/// Provider-specific delivery evidence the broker attached to an inbound message, such as RabbitMQ death history.
/// The neutral verifier only asks whether redelivery history exists; interpreting it belongs to the provider's
/// <see cref="ITenantWrightRedeliveryCustodyVerifier"/>.
/// </summary>
public abstract class TenantWrightTransportEvidence
{
    public abstract bool HasRedeliveryHistory { get; }
}

/// <summary>Evidence for a transport that never attaches broker redelivery history.</summary>
public sealed class NoTenantWrightTransportEvidence : TenantWrightTransportEvidence
{
    public static NoTenantWrightTransportEvidence Instance { get; } = new();

    private NoTenantWrightTransportEvidence()
    {
    }

    public override bool HasRedeliveryHistory => false;
}

/// <summary>
/// A transport-neutral inbound delivery, built by the provider adapter before any TenantWright code runs.
/// </summary>
/// <param name="EndpointName">The receive endpoint (queue) the broker delivered from.</param>
/// <param name="Headers">
/// Every <see cref="TenantWrightTrustHeaders"/> name the delivery carried, in any provider header location. A key whose
/// value is <see langword="null"/> is present but unreadable as text, which still counts as present custody evidence.
/// </param>
/// <param name="BrokerActor">The broker-authenticated publisher (RabbitMQ validated <c>user_id</c>), when reported.</param>
/// <param name="DeliveryAttempt">The provider's one-based delivery attempt, for diagnostics.</param>
public sealed record TenantWrightInboundMessage(
    string EndpointName,
    Guid? MessageId,
    object Message,
    IReadOnlyDictionary<string, string?> Headers,
    string? BrokerActor,
    int DeliveryAttempt,
    TenantWrightTransportEvidence TransportEvidence)
{
    public Type MessageType => Message.GetType();
}

public enum TenantWrightInboundCustody
{
    /// <summary>Delivered by the origin producer under its own broker identity.</summary>
    Direct = 1,

    /// <summary>Redelivered by the consuming endpoint owner through the provider's delayed-redelivery custody.</summary>
    Redelivered = 2,

    /// <summary>Re-sent by the protected replay principal under a signed, single-use replay authorization.</summary>
    Replayed = 3
}

public sealed record TenantWrightVerifiedInboundMessage(
    Guid MessageId,
    string Endpoint,
    OriginAttestation Origin,
    Guid? TenantId,
    TenantWrightInboundCustody Custody);

/// <summary>
/// Verifies provider-owned broker redelivery history for a relay attestation the neutral verifier already
/// validated. A provider that does not register one cannot have redelivered custody accepted.
/// </summary>
public interface ITenantWrightRedeliveryCustodyVerifier
{
    /// <exception cref="MessagingSecurityException">The broker history does not prove the signed relay.</exception>
    void VerifyRedeliveryHistory(TenantWrightInboundMessage message, RelayAttestation relay, DateTimeOffset now);
}

/// <summary>
/// Verifies provider-owned preconditions of a protected replay delivery. A provider that does not register one cannot
/// have replay custody accepted.
/// </summary>
public interface ITenantWrightReplayCustodyVerifier
{
    /// <exception cref="MessagingSecurityException">The delivery is not a valid replay for this provider.</exception>
    void VerifyReplayDelivery(TenantWrightInboundMessage message, ReplayAttestation replay, DateTimeOffset now);
}

/// <summary>
/// The transport-neutral inbound trust boundary. Every check here is independent of the broker library: endpoint
/// binding, origin attestation, producer allow-list, payload digest, signature, freshness, custody and tenancy.
/// </summary>
/// <remarks>
/// Custody accepts exactly one of three shapes and fails closed otherwise:
/// <list type="bullet">
/// <item>direct: the broker actor is the signed origin producer and no relay, replay, terminal or broker
/// redelivery evidence is present;</item>
/// <item>redelivered: the broker actor is the endpoint owner (or was dropped by the broker), a complete relay
/// attestation signed by the endpoint owner chains to the origin signature, and the provider's registered
/// <see cref="ITenantWrightRedeliveryCustodyVerifier"/> proves the broker history;</item>
/// <item>replayed: the broker actor is the replay principal, a replay attestation bound to this endpoint chains to the
/// original custody, the provider's registered <see cref="ITenantWrightReplayCustodyVerifier"/> accepts the delivery, and
/// the replay ID is consumed exactly once.</item>
/// </list>
/// </remarks>
public sealed class TenantWrightInboundTrustVerifier
{
    private readonly TenantWrightMessageCatalog _catalog;
    private readonly TenantWrightAttestationService _attestations;
    private readonly IProtectedReplayIdempotencyStore _replayIds;
    private readonly MessagingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ITenantWrightRedeliveryCustodyVerifier? _redeliveryVerifier;
    private readonly ITenantWrightReplayCustodyVerifier? _replayVerifier;

    public TenantWrightInboundTrustVerifier(
        TenantWrightMessageCatalog catalog,
        TenantWrightAttestationService attestations,
        IProtectedReplayIdempotencyStore replayIds,
        IOptions<MessagingOptions> options,
        TimeProvider timeProvider,
        IEnumerable<ITenantWrightRedeliveryCustodyVerifier> redeliveryVerifiers,
        IEnumerable<ITenantWrightReplayCustodyVerifier> replayVerifiers)
    {
        _catalog = catalog;
        _attestations = attestations;
        _replayIds = replayIds;
        _options = options.Value;
        _timeProvider = timeProvider;
        _redeliveryVerifier = AtMostOne(redeliveryVerifiers);
        _replayVerifier = AtMostOne(replayVerifiers);
    }

    public async Task<TenantWrightVerifiedInboundMessage> VerifyAsync(
        TenantWrightInboundMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            var runtimeType = message.MessageType;
            var route = _catalog.GetRoute(runtimeType);
            var endpoint = _catalog.GetReceiveEndpoint(message.EndpointName);
            if (!endpoint.ContractTypes.Contains(runtimeType))
                Reject(MessagingSecurityException.UntrustedCustody);

            var identity = TenantWrightMessageContracts.GetIdentity(runtimeType);
            var origin = ReadOrigin(message, identity);
            if (!route.AllowedOriginPrincipals.Contains(origin.OriginPrincipal))
                Reject(MessagingSecurityException.UntrustedProducer);
            var canonicalPayload = _catalog.SerializeCanonical(message.Message, runtimeType);
            if (!FixedDigestEquals(origin.PayloadSha256, TenantWrightAttestationService.Sha256(canonicalPayload)))
                Reject(MessagingSecurityException.InvalidSignature);
            if (!_attestations.Verify(origin))
                Reject(MessagingSecurityException.InvalidSignature);

            var now = _timeProvider.GetUtcNow();
            var trust = _options.Trust;
            if (origin.IssuedAt > now + trust.MaximumFutureClockSkew || origin.IssuedAt < now - trust.MaximumOriginAge)
                Reject(MessagingSecurityException.InvalidContext);

            var custody = await VerifyCustodyAsync(message, endpoint, origin, now, cancellationToken);
            var tenantId = TenantWrightMessageContracts.VerifyTenant(
                message.Message,
                TenantWrightMessageContracts.GetMetadata(runtimeType),
                origin);
            return new TenantWrightVerifiedInboundMessage(origin.MessageId, endpoint.Name, origin, tenantId, custody);
        }
        catch (MessagingSecurityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);
        }
    }

    private static OriginAttestation ReadOrigin(TenantWrightInboundMessage message, string identity)
    {
        if (!string.Equals(
                TenantWrightTrustHeaderMap.Required(message.Headers, TenantWrightTrustHeaders.ContextVersion),
                "1",
                StringComparison.Ordinal) ||
            !message.MessageId.HasValue)
            Reject(MessagingSecurityException.InvalidContext);
        return TenantWrightTrustHeaderMap.ReadOrigin(message.Headers, message.MessageId!.Value, identity);
    }

    private async Task<TenantWrightInboundCustody> VerifyCustodyAsync(
        TenantWrightInboundMessage message,
        TenantWrightReceiveEndpoint endpoint,
        OriginAttestation origin,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var actor = message.BrokerActor;
        var headers = message.Headers;
        var hasRedeliveryHistory = message.TransportEvidence.HasRedeliveryHistory;
        var hasRelayEvidence = HasAny(headers, TenantWrightTrustHeaders.Relay);
        var hasReplayEvidence = HasAny(headers, TenantWrightTrustHeaders.Replay);
        var hasTerminalEvidence = HasAny(headers, TenantWrightTrustHeaders.Terminal);

        if (string.Equals(actor, _options.Replay.Actor, StringComparison.Ordinal))
        {
            if (_replayVerifier is null)
                Reject(MessagingSecurityException.ReplayRejected);
            if (!hasReplayEvidence || hasRedeliveryHistory || hasTerminalEvidence ||
                (hasRelayEvidence && !HasAll(headers, TenantWrightTrustHeaders.Relay)))
                Reject(MessagingSecurityException.ReplayRejected);

            var replay = new ReplayAttestation(
                TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.ReplayTerminalRecordSha256),
                TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.ReplayOriginalChainSha256),
                origin.MessageId,
                origin.PayloadSha256,
                TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.ReplayDestination),
                TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.ReplayId),
                TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.ReplayAuditId),
                TenantWrightTrustHeaderMap.ParseInstant(headers, TenantWrightTrustHeaders.ReplayAuthorizedAt),
                TenantWrightTrustHeaderMap.ParseInstant(headers, TenantWrightTrustHeaders.ReplayExpiry),
                actor!,
                TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.ReplayKeyId),
                TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.ReplaySignature));
            if (!string.Equals(replay.Destination, endpoint.Name, StringComparison.Ordinal) ||
                replay.ExpiresAt <= now || replay.AuthorizedAt > now || replay.ExpiresAt <= replay.AuthorizedAt ||
                replay.ExpiresAt - replay.AuthorizedAt > _options.Replay.MaximumAuthorizationLifetime ||
                !_attestations.Verify(replay))
                Reject(MessagingSecurityException.ReplayRejected);

            var expectedChainDigest = TenantWrightAttestationService.SignatureDigest(origin.Signature);
            if (hasRelayEvidence)
            {
                var relay = ReadRelay(headers, origin, endpoint);
                if (relay.Attempt is < 1 || relay.Attempt > _options.Delivery.DelayedRedeliverySeconds.Length ||
                    relay.ExpiresAt <= relay.NotBefore ||
                    relay.ExpiresAt - relay.NotBefore > _options.Trust.MaximumRelayLifetime ||
                    !_attestations.Verify(relay))
                    Reject(MessagingSecurityException.ReplayRejected);
                expectedChainDigest = TenantWrightAttestationService.SignatureDigest(relay.Signature);
            }

            if (!FixedDigestEquals(replay.OriginalChainSha256, expectedChainDigest))
                Reject(MessagingSecurityException.ReplayRejected);

            _replayVerifier!.VerifyReplayDelivery(message, replay, now);

            var consumption = new ProtectedReplayConsumption(
                replay.ReplayId, replay.Destination, replay.AuditId, replay.ExpiresAt, now);
            if (!await _replayIds.TryConsumeIssuedAsync(consumption, cancellationToken))
                Reject(MessagingSecurityException.ReplayRejected);
            return TenantWrightInboundCustody.Replayed;
        }

        // A broker can omit the publisher identity when it performs the delayed relay itself. Only the endpoint-owned,
        // signed relay plus provider-verified broker history can compensate for that loss.
        if ((string.Equals(actor, endpoint.OwnerPrincipal, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(actor)) &&
            (hasRelayEvidence || hasRedeliveryHistory))
        {
            if (_redeliveryVerifier is null ||
                !hasRelayEvidence || !hasRedeliveryHistory || hasReplayEvidence || hasTerminalEvidence)
                Reject(MessagingSecurityException.UntrustedCustody);

            var relay = ReadRelay(headers, origin, endpoint);
            var trust = _options.Trust;
            if (relay.Attempt is < 1 || relay.Attempt > _options.Delivery.DelayedRedeliverySeconds.Length)
                Reject(MessagingSecurityException.UntrustedCustody);
            if (relay.ExpiresAt <= relay.NotBefore || relay.ExpiresAt - relay.NotBefore > trust.MaximumRelayLifetime ||
                relay.NotBefore > now + trust.MaximumFutureClockSkew || relay.ExpiresAt <= now)
                Reject(MessagingSecurityException.UntrustedCustody);
            if (!_attestations.Verify(relay))
                Reject(MessagingSecurityException.UntrustedCustody);

            _redeliveryVerifier!.VerifyRedeliveryHistory(message, relay, now);
            return TenantWrightInboundCustody.Redelivered;
        }

        if (string.Equals(actor, origin.OriginPrincipal, StringComparison.Ordinal) &&
            !hasRedeliveryHistory && !hasRelayEvidence && !hasReplayEvidence && !hasTerminalEvidence)
            return TenantWrightInboundCustody.Direct;

        throw new MessagingSecurityException(MessagingSecurityException.UntrustedProducer);
    }

    /// <summary>The relay attestation as the endpoint owner must have signed it for this exact origin and endpoint.</summary>
    private static RelayAttestation ReadRelay(
        IReadOnlyDictionary<string, string?> headers,
        OriginAttestation origin,
        TenantWrightReceiveEndpoint endpoint) =>
        new(origin.MessageId,
            origin.PayloadSha256,
            TenantWrightAttestationService.SignatureDigest(origin.Signature),
            endpoint.Name,
            TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.RelayId),
            int.Parse(
                TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.RelayAttempt),
                NumberStyles.None,
                CultureInfo.InvariantCulture),
            TenantWrightTrustHeaderMap.ParseInstant(headers, TenantWrightTrustHeaders.RelayNotBefore),
            TenantWrightTrustHeaderMap.ParseInstant(headers, TenantWrightTrustHeaders.RelayExpiry),
            endpoint.OwnerPrincipal,
            TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.RelayKeyId),
            TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.RelaySignature));

    private static bool HasAny(IReadOnlyDictionary<string, string?> headers, IReadOnlyList<string> names) =>
        names.Any(headers.ContainsKey);

    private static bool HasAll(IReadOnlyDictionary<string, string?> headers, IReadOnlyList<string> names) =>
        names.All(headers.ContainsKey);

    private static bool FixedDigestEquals(string left, string right)
    {
        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static T? AtMostOne<T>(IEnumerable<T> registrations) where T : class =>
        registrations.ToArray() switch
        {
            [] => null,
            [var single] => single,
            _ => throw new InvalidOperationException(
                $"At most one {typeof(T).Name} may be registered: one messaging provider is active per deployment.")
        };

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(string reason) => throw new MessagingSecurityException(reason);
}
