using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.Wolverine;

/// <summary>Replays to one endpoint exchange as the dedicated replay principal, with a broker confirmation.</summary>
internal sealed class WolverineProtectedReplayTransport(
    RabbitMqConfirmedPublisher publisher,
    TenantWrightMessageCatalog catalog,
    TenantWrightAttestationService attestations,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider) : IProtectedReplayTransport
{
    public async Task SendAsync(ProtectedReplayDispatch dispatch, CancellationToken cancellationToken)
    {
        var (properties, body) = CreatePublication(dispatch, catalog, attestations, options.Value, timeProvider.GetUtcNow());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WolverineTerminalFailure.PublishTimeout);
        await publisher.PublishAsync(dispatch.Attestation.Destination, properties, body, timeout.Token);
    }

    internal static (BasicProperties Properties, byte[] Body) CreatePublication(
        ProtectedReplayDispatch dispatch,
        TenantWrightMessageCatalog catalog,
        TenantWrightAttestationService attestations,
        MessagingOptions options,
        DateTimeOffset now)
    {
        var replay = dispatch.Attestation;
        var material = dispatch.Material;
        var endpoint = catalog.GetEndpointRoute(replay.Destination);
        var route = catalog.GetRoute(material.ContractType);
        if (material.Message.GetType() != material.ContractType)
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);
        var identity = TenantWrightMessageContracts.GetIdentity(material.ContractType);
        var body = catalog.SerializeCanonical(material.Message, material.ContractType);
        if (attestations.Actor != options.Replay.Actor || replay.Actor != attestations.Actor ||
            !attestations.Verify(replay) || !attestations.Verify(material.Origin) || material.Relay is not null ||
            !endpoint.ContractTypes.Contains(material.ContractType) ||
            !route.AllowedOriginPrincipals.Contains(material.Origin.OriginPrincipal) ||
            material.Origin.ContractIdentity != identity || material.Summary.ContractIdentity != identity ||
            material.PayloadSha256 != material.Origin.PayloadSha256 || material.OriginalChainSha256 != replay.OriginalChainSha256 ||
            replay.MessageId != material.MessageId || material.MessageId != material.Origin.MessageId ||
            material.Summary.Endpoint != replay.Destination || !material.Summary.ProvenanceVerified ||
            material.Summary.ExpiresAt <= now || replay.ExpiresAt <= now || replay.AuthorizedAt > now ||
            replay.ExpiresAt <= replay.AuthorizedAt || replay.ExpiresAt - replay.AuthorizedAt > options.Replay.MaximumAuthorizationLifetime ||
            replay.TerminalRecordSha256 != material.TerminalRecordSha256 ||
            replay.OriginalChainSha256 != TenantWrightAttestationService.SignatureDigest(material.Origin.Signature) ||
            replay.PayloadSha256 != material.Origin.PayloadSha256 ||
            replay.PayloadSha256 != TenantWrightAttestationService.Sha256(body))
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);

        TenantWrightMessageContracts.VerifyTenant(material.Message, TenantWrightMessageContracts.GetMetadata(material.ContractType), material.Origin);
        var headers = TenantWrightTrustHeaderMap.FromOrigin(material.Origin)
            .ToDictionary(pair => pair.Key, object? (pair) => pair.Value, StringComparer.Ordinal);
        foreach (var pair in TenantWrightTrustHeaderMap.FromReplay(replay))
            headers.Add(pair.Key, pair.Value);
        return (new BasicProperties
        {
            MessageId = material.MessageId.ToString("D"), UserId = replay.Actor,
            Type = TenantWrightMessageContracts.GetIdentity(material.ContractType),
            ContentType = TenantWrightCanonicalMessageSerializer.JsonContentType,
            DeliveryMode = DeliveryModes.Persistent, Headers = headers,
            Timestamp = new AmqpTimestamp(now.ToUnixTimeSeconds())
        }, body);
    }
}

internal sealed class WolverineReplayCustodyVerifier : ITenantWrightReplayCustodyVerifier
{
    public void VerifyReplayDelivery(TenantWrightInboundMessage message, ReplayAttestation replay, DateTimeOffset now)
    {
        // Only the adapter's mapper can supply this evidence; a neutral/fabricated evidence object is insufficient.
        if (message.TransportEvidence is not WolverineRabbitMqTransportEvidence { HasRedeliveryHistory: false } ||
            TenantWrightTrustHeaders.Relay.Any(message.Headers.ContainsKey))
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);
    }
}
