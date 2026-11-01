using System.Security.Cryptography;

namespace Shared.EventBus.Messaging;

public sealed record OriginAttestation(
    Guid MessageId, string ContractIdentity, string OriginPrincipal, string Scope, string? TenantId,
    DateTimeOffset IssuedAt, string PayloadSha256, string KeyId, string Signature);

public sealed record RelayAttestation(
    Guid MessageId, string PayloadSha256, string PriorSignatureSha256, string Endpoint, string RelayId,
    int Attempt, DateTimeOffset NotBefore, DateTimeOffset ExpiresAt, string Actor, string KeyId, string Signature);

public sealed record TerminalAttestation(
    string RawMessageSha256, string OriginChainSha256, string? RelayChainSha256, string Endpoint,
    string TerminalId, string Classification, DateTimeOffset Timestamp, string Actor, string KeyId, string Signature);

public sealed record ReplayAttestation(
    string TerminalRecordSha256, string OriginalChainSha256, Guid MessageId, string PayloadSha256,
    string Destination, string ReplayId, string AuditId, DateTimeOffset AuthorizedAt, DateTimeOffset ExpiresAt,
    string Actor, string KeyId, string Signature);

public sealed class TenantWrightAttestationService(ITenantWrightMessageSigner signer, ITenantWrightPublicKeyRegistry keys)
{
    /// <summary>The exact producer principal this host signs as.</summary>
    public string Actor => signer.Actor;

    public OriginAttestation CreateOrigin(Guid messageId, string contract, string scope, string? tenantId, DateTimeOffset issuedAt, string payloadDigest)
    {
        var unsigned = new OriginAttestation(messageId, contract, signer.Actor, scope, tenantId, issuedAt, payloadDigest, signer.KeyId, string.Empty);
        return unsigned with { Signature = Convert.ToBase64String(signer.Sign(OriginInput(unsigned))) };
    }

    public RelayAttestation CreateRelay(Guid messageId, string payloadDigest, string priorSignatureDigest, string endpoint,
        string relayId, int attempt, DateTimeOffset notBefore, DateTimeOffset expiresAt)
    {
        var unsigned = new RelayAttestation(messageId, payloadDigest, priorSignatureDigest, endpoint, relayId, attempt,
            notBefore, expiresAt, signer.Actor, signer.KeyId, string.Empty);
        return unsigned with { Signature = Convert.ToBase64String(signer.Sign(RelayInput(unsigned))) };
    }

    public TerminalAttestation CreateTerminal(string rawDigest, string originChainDigest, string? relayChainDigest,
        string endpoint, string terminalId, string classification, DateTimeOffset timestamp)
    {
        var unsigned = new TerminalAttestation(rawDigest, originChainDigest, relayChainDigest, endpoint, terminalId,
            classification, timestamp, signer.Actor, signer.KeyId, string.Empty);
        return unsigned with { Signature = Convert.ToBase64String(signer.Sign(TerminalInput(unsigned))) };
    }

    public ReplayAttestation CreateReplay(string terminalDigest, string originalChainDigest, Guid messageId,
        string payloadDigest, string destination, string replayId, string auditId, DateTimeOffset authorizedAt, DateTimeOffset expiresAt)
    {
        var unsigned = new ReplayAttestation(terminalDigest, originalChainDigest, messageId, payloadDigest, destination,
            replayId, auditId, authorizedAt, expiresAt, signer.Actor, signer.KeyId, string.Empty);
        return unsigned with { Signature = Convert.ToBase64String(signer.Sign(ReplayInput(unsigned))) };
    }

    public bool Verify(OriginAttestation value) => Verify(value.KeyId, value.OriginPrincipal, OriginInput(value), value.Signature);
    public bool Verify(RelayAttestation value) => Verify(value.KeyId, value.Actor, RelayInput(value), value.Signature);
    public bool Verify(TerminalAttestation value) => Verify(value.KeyId, value.Actor, TerminalInput(value), value.Signature);
    public bool Verify(ReplayAttestation value) => Verify(value.KeyId, value.Actor, ReplayInput(value), value.Signature);

    public static string Sha256(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));

    public static string SignatureDigest(string signature) => Sha256(Convert.FromBase64String(signature));

    private bool Verify(string keyId, string actor, byte[] input, string signature)
    {
        try { return keys.Verify(keyId, actor, input, Convert.FromBase64String(signature)); }
        catch (FormatException) { return false; }
    }

    private static byte[] OriginInput(OriginAttestation value) => CanonicalAttestationInput.Create("tenantwright-origin-v1",
        value.MessageId.ToString("D"), value.ContractIdentity, value.OriginPrincipal, value.Scope, value.TenantId ?? string.Empty,
        value.IssuedAt.UtcDateTime.ToString("O"), value.PayloadSha256, value.KeyId);
    private static byte[] RelayInput(RelayAttestation value) => CanonicalAttestationInput.Create("tenantwright-relay-v1",
        value.PriorSignatureSha256, value.MessageId.ToString("D"), value.PayloadSha256, value.Endpoint, value.RelayId,
        value.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture), value.NotBefore.UtcDateTime.ToString("O"),
        value.ExpiresAt.UtcDateTime.ToString("O"), value.Actor, value.KeyId);
    private static byte[] TerminalInput(TerminalAttestation value) => CanonicalAttestationInput.Create("tenantwright-terminal-v1",
        value.RawMessageSha256, value.OriginChainSha256, value.RelayChainSha256 ?? string.Empty, value.Endpoint,
        value.TerminalId, value.Classification, value.Timestamp.UtcDateTime.ToString("O"), value.Actor, value.KeyId);
    private static byte[] ReplayInput(ReplayAttestation value) => CanonicalAttestationInput.Create("tenantwright-replay-v1",
        value.TerminalRecordSha256, value.OriginalChainSha256, value.MessageId.ToString("D"), value.PayloadSha256,
        value.Destination, value.ReplayId, value.AuditId, value.AuthorizedAt.UtcDateTime.ToString("O"),
        value.ExpiresAt.UtcDateTime.ToString("O"), value.Actor, value.KeyId);
}
