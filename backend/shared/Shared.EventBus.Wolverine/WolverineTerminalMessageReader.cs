using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// Validates an endpoint's signed dead-letter snapshot for an operator-owned <see cref="IProtectedTerminalStore"/>.
/// The store must read from the named endpoint's dead-letter queue without discarding its only copy before recovery.
/// Native unsigned quarantine, expired origins and revoked keys never become replay authority.
/// </summary>
public sealed class WolverineTerminalMessageReader(
    TenantWrightMessageCatalog catalog,
    TenantWrightAttestationService attestations,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider)
{
    public ProtectedReplayMaterial Read(string endpointName, IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(properties);
        try
        {
            return ReadVerified(endpointName, properties, body);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or OverflowException)
        {
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);
        }
    }

    private ProtectedReplayMaterial ReadVerified(string endpointName, IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body)
    {
        var endpoint = catalog.GetEndpointRoute(endpointName);
        if (!string.Equals(properties.UserId, endpoint.OwnerPrincipal, StringComparison.Ordinal) ||
            !string.Equals(properties.ContentType, TenantWrightCanonicalMessageSerializer.JsonContentType, StringComparison.Ordinal) ||
            !Guid.TryParseExact(properties.MessageId, "D", out var messageId) ||
            properties.Headers?.ContainsKey("x-death") == true)
            throw Rejected();

        var headers = TenantWrightRabbitMqEnvelopeMapper.ReadTrustHeaders(properties.Headers);
        if (TenantWrightTrustHeaders.Relay.Any(headers.ContainsKey) || TenantWrightTrustHeaders.Replay.Any(headers.ContainsKey))
            throw Rejected();
        var route = catalog.GetRouteByIdentity(properties.Type ?? string.Empty);
        if (!endpoint.ContractTypes.Contains(route.ContractType))
            throw Rejected();
        var origin = TenantWrightTrustHeaderMap.ReadOrigin(headers, messageId, properties.Type!);
        var terminal = new TerminalAttestation(
            TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.TerminalRawMessageSha256),
            TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.TerminalOriginChainSha256),
            null, endpoint.Name,
            TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.TerminalId),
            TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.TerminalClassification),
            TenantWrightTrustHeaderMap.ParseInstant(headers, TenantWrightTrustHeaders.TerminalTimestamp),
            endpoint.OwnerPrincipal,
            TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.TerminalKeyId),
            TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.TerminalSignature));
        var now = timeProvider.GetUtcNow();
        var settings = options.Value;
        if (!string.IsNullOrEmpty(TenantWrightTrustHeaderMap.Required(headers, TenantWrightTrustHeaders.TerminalRelayChainSha256)) ||
            !Guid.TryParseExact(terminal.TerminalId, "D", out _) || terminal.Classification != "fault" ||
            terminal.Timestamp > now + settings.Trust.MaximumFutureClockSkew ||
            terminal.Timestamp + settings.Replay.TerminalRetention <= now ||
            origin.IssuedAt > now + settings.Trust.MaximumFutureClockSkew ||
            origin.IssuedAt < now - settings.Trust.MaximumOriginAge ||
            !route.AllowedOriginPrincipals.Contains(origin.OriginPrincipal) ||
            !attestations.Verify(origin) || !attestations.Verify(terminal) ||
            terminal.RawMessageSha256 != TenantWrightAttestationService.Sha256(body.Span) ||
            terminal.OriginChainSha256 != TenantWrightAttestationService.SignatureDigest(origin.Signature))
            throw Rejected();

        var message = catalog.DeserializeCanonical(body.Span, route.ContractType);
        if (origin.PayloadSha256 != TenantWrightAttestationService.Sha256(catalog.SerializeCanonical(message, route.ContractType)))
            throw Rejected();
        TenantWrightMessageContracts.VerifyTenant(message, TenantWrightMessageContracts.GetMetadata(route.ContractType), origin);
        return new ProtectedReplayMaterial(
            new ProtectedTerminalSummary(terminal.TerminalId, endpointName, terminal.Classification, terminal.Timestamp,
                terminal.Timestamp + settings.Replay.TerminalRetention, true, properties.Type!),
            messageId, origin.PayloadSha256, TenantWrightAttestationService.SignatureDigest(terminal.Signature),
            terminal.OriginChainSha256, route.ContractType, message, origin, null);
    }

    private static MessagingSecurityException Rejected() => new(MessagingSecurityException.ReplayRejected);
}
