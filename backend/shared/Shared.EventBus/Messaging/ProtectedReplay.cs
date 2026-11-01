using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Shared.EventContracts;

namespace Shared.EventBus.Messaging;

public sealed record ProtectedTerminalSummary(
    string TerminalId, string Endpoint, string Classification, DateTimeOffset RecordedAt,
    DateTimeOffset ExpiresAt, bool ProvenanceVerified, string ContractIdentity);

public sealed record ProtectedReplayRequest(
    string TerminalId, string Destination, string Reason, string AuthorizationId, string AuditId, string ReplayId);

public sealed record ProtectedReplayMaterial(
    ProtectedTerminalSummary Summary,
    Guid MessageId,
    string PayloadSha256,
    string TerminalRecordSha256,
    string OriginalChainSha256,
    Type ContractType,
    object Message,
    OriginAttestation Origin,
    RelayAttestation? Relay);

public sealed record ProtectedReplayIssue(
    string ReplayId,
    string Destination,
    string AuditId,
    DateTimeOffset ExpiresAt);

public sealed record ProtectedReplayConsumption(
    string ReplayId,
    string Destination,
    string AuditId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ConsumedAt);

/// <summary>
/// One authorized replay handed to the active provider's <see cref="IProtectedReplayTransport"/>. The provider re-sends
/// <see cref="ProtectedReplayMaterial.Message"/> to <see cref="ReplayAttestation.Destination"/> under the replay
/// principal, carrying the original origin (and relay, when present) custody plus <paramref name="Attestation"/>.
/// </summary>
public sealed record ProtectedReplayDispatch(
    ProtectedReplayMaterial Material,
    ReplayAttestation Attestation);

public interface IProtectedTerminalStore
{
    Task<ProtectedReplayMaterial?> FindAsync(string terminalId, CancellationToken cancellationToken);
}

public interface IProtectedReplayAuthorization
{
    Task AuthorizeAsync(ProtectedReplayRequest request, ProtectedTerminalSummary terminal, CancellationToken cancellationToken);
}

public interface IProtectedReplayAudit
{
    Task RecordAsync(ProtectedReplayRequest request, ProtectedTerminalSummary terminal, CancellationToken cancellationToken);
}

public interface IProtectedReplayIdempotencyStore
{
    /// <summary>
    /// Atomically creates an issued replay ID with its immutable destination, audit, and expiry binding.
    /// An issued ID remains reserved even when transport outcome is ambiguous.
    /// </summary>
    Task<bool> TryIssueAsync(ProtectedReplayIssue issue, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically transitions the exactly matching, unexpired issued replay ID to consumed.
    /// Missing, mismatched, expired, or previously consumed IDs return false.
    /// </summary>
    Task<bool> TryConsumeIssuedAsync(ProtectedReplayConsumption consumption, CancellationToken cancellationToken);
}

public interface IProtectedReplayTransport
{
    Task SendAsync(ProtectedReplayDispatch dispatch, CancellationToken cancellationToken);
}

public sealed class ProtectedReplayService(
    IProtectedTerminalStore terminals,
    IProtectedReplayAuthorization authorization,
    IProtectedReplayAudit audit,
    IProtectedReplayIdempotencyStore replayIds,
    IProtectedReplayTransport transport,
    TenantWrightMessageCatalog catalog,
    TenantWrightAttestationService attestations,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider)
{
    public async Task ReplayAsync(ProtectedReplayRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TerminalId) || string.IsNullOrWhiteSpace(request.Destination) ||
            string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.AuthorizationId) ||
            string.IsNullOrWhiteSpace(request.AuditId) || string.IsNullOrWhiteSpace(request.ReplayId))
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);

        var material = await terminals.FindAsync(request.TerminalId, cancellationToken)
            ?? throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);
        var now = timeProvider.GetUtcNow();
        if (!material.Summary.ProvenanceVerified || material.Summary.ExpiresAt <= now ||
            !string.Equals(material.Summary.TerminalId, request.TerminalId, StringComparison.Ordinal) ||
            !string.Equals(material.Summary.Endpoint, request.Destination, StringComparison.Ordinal) ||
            !string.Equals(options.Value.ProducerPrincipal, options.Value.Replay.Actor, StringComparison.Ordinal))
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);

        var runtimeType = material.Message.GetType();
        if (runtimeType != material.ContractType)
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);
        var route = catalog.GetRoute(runtimeType);
        var endpoint = catalog.GetEndpointRoute(request.Destination);
        if (!endpoint.ContractTypes.Contains(runtimeType) || !route.AllowedOriginPrincipals.Contains(material.Origin.OriginPrincipal))
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);

        ValidateMaterial(material, runtimeType, request.Destination, attestations, catalog);
        await authorization.AuthorizeAsync(request, material.Summary, cancellationToken);
        await audit.RecordAsync(request, material.Summary, cancellationToken);

        var expiresAt = now + options.Value.Replay.MaximumAuthorizationLifetime;
        var issue = new ProtectedReplayIssue(request.ReplayId, request.Destination, request.AuditId, expiresAt);
        if (!await replayIds.TryIssueAsync(issue, cancellationToken))
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);

        var attestation = attestations.CreateReplay(material.TerminalRecordSha256, material.OriginalChainSha256,
            material.MessageId, material.PayloadSha256, request.Destination, request.ReplayId, request.AuditId, now, expiresAt);
        if (!string.Equals(attestation.Actor, options.Value.Replay.Actor, StringComparison.Ordinal))
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);

        await transport.SendAsync(new ProtectedReplayDispatch(material, attestation), cancellationToken);
    }

    private static void ValidateMaterial(
        ProtectedReplayMaterial material,
        Type runtimeType,
        string destination,
        TenantWrightAttestationService attestations, TenantWrightMessageCatalog catalog)
    {
        if (material.MessageId != material.Origin.MessageId ||
            !string.Equals(material.PayloadSha256, material.Origin.PayloadSha256, StringComparison.Ordinal) ||
            !attestations.Verify(material.Origin))
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);

        var payloadDigest = TenantWrightAttestationService.Sha256(
            catalog.SerializeCanonical(material.Message, runtimeType));
        if (!FixedDigestEquals(payloadDigest, material.PayloadSha256))
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);

        var expectedChainDigest = TenantWrightAttestationService.SignatureDigest(material.Origin.Signature);
        if (material.Relay is not null)
        {
            if (material.Relay.MessageId != material.MessageId ||
                !string.Equals(material.Relay.PayloadSha256, material.PayloadSha256, StringComparison.Ordinal) ||
                !string.Equals(material.Relay.Endpoint, destination, StringComparison.Ordinal) ||
                !attestations.Verify(material.Relay))
                throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);
            expectedChainDigest = TenantWrightAttestationService.SignatureDigest(material.Relay.Signature);
        }

        if (!FixedDigestEquals(expectedChainDigest, material.OriginalChainSha256))
            throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);
    }

    private static bool FixedDigestEquals(string left, string right)
    {
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
