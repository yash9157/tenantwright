using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Shared.EventBus.Messaging;

public static class TenantWrightTrustHeaders
{
    public const string ContextVersion = "x-tenantwright-context-version";
    public const string Producer = "x-tenantwright-producer";
    public const string Scope = "x-tenantwright-scope";
    public const string TenantId = "x-tenantwright-tenant-id";
    public const string OriginKeyId = "x-tenantwright-origin-key-id";
    public const string OriginIssuedAt = "x-tenantwright-origin-issued-at";
    public const string OriginPayloadSha256 = "x-tenantwright-origin-payload-sha256";
    public const string OriginSignature = "x-tenantwright-origin-signature";
    public const string RelayId = "x-tenantwright-relay-id";
    public const string RelayAttempt = "x-tenantwright-relay-attempt";
    public const string RelayNotBefore = "x-tenantwright-relay-not-before";
    public const string RelayExpiry = "x-tenantwright-relay-expiry";
    public const string RelayKeyId = "x-tenantwright-relay-key-id";
    public const string RelaySignature = "x-tenantwright-relay-signature";
    public const string TerminalRawMessageSha256 = "x-tenantwright-terminal-raw-message-sha256";
    public const string TerminalOriginChainSha256 = "x-tenantwright-terminal-origin-chain-sha256";
    public const string TerminalRelayChainSha256 = "x-tenantwright-terminal-relay-chain-sha256";
    public const string TerminalId = "x-tenantwright-terminal-id";
    public const string TerminalClassification = "x-tenantwright-terminal-classification";
    public const string TerminalTimestamp = "x-tenantwright-terminal-timestamp";
    public const string TerminalKeyId = "x-tenantwright-terminal-key-id";
    public const string TerminalSignature = "x-tenantwright-terminal-signature";
    public const string ReplayTerminalRecordSha256 = "x-tenantwright-replay-terminal-record-sha256";
    public const string ReplayOriginalChainSha256 = "x-tenantwright-replay-original-chain-sha256";
    public const string ReplayId = "x-tenantwright-replay-id";
    public const string ReplayAuditId = "x-tenantwright-replay-audit-id";
    public const string ReplayAuthorizedAt = "x-tenantwright-replay-authorized-at";
    public const string ReplayExpiry = "x-tenantwright-replay-expiry";
    public const string ReplayDestination = "x-tenantwright-replay-destination";
    public const string ReplayKeyId = "x-tenantwright-replay-key-id";
    public const string ReplaySignature = "x-tenantwright-replay-signature";

    public static bool IsTerminalHeader(string header) =>
        header.StartsWith("x-tenantwright-terminal-", StringComparison.Ordinal);

    public static readonly IReadOnlyList<string> Relay =
    [
        RelayId, RelayAttempt, RelayNotBefore, RelayExpiry, RelayKeyId, RelaySignature
    ];

    public static readonly IReadOnlyList<string> Terminal =
    [
        TerminalRawMessageSha256, TerminalOriginChainSha256, TerminalRelayChainSha256,
        TerminalId, TerminalClassification, TerminalTimestamp, TerminalKeyId, TerminalSignature
    ];

    public static readonly IReadOnlyList<string> Replay =
    [
        ReplayTerminalRecordSha256, ReplayOriginalChainSha256,
        ReplayId, ReplayAuditId, ReplayAuthorizedAt, ReplayExpiry, ReplayDestination, ReplayKeyId, ReplaySignature
    ];

    public static readonly IReadOnlyList<string> All =
    [
        ContextVersion, Producer, Scope, TenantId, OriginKeyId, OriginIssuedAt, OriginPayloadSha256, OriginSignature,
        RelayId, RelayAttempt, RelayNotBefore, RelayExpiry, RelayKeyId, RelaySignature,
        TerminalRawMessageSha256, TerminalOriginChainSha256, TerminalRelayChainSha256,
        TerminalId, TerminalClassification, TerminalTimestamp, TerminalKeyId, TerminalSignature,
        ReplayTerminalRecordSha256, ReplayOriginalChainSha256,
        ReplayId, ReplayAuditId, ReplayAuthorizedAt, ReplayExpiry, ReplayDestination, ReplayKeyId, ReplaySignature
    ];

    public static readonly IReadOnlyList<string> Custody =
    [
        RelayId, RelayAttempt, RelayNotBefore, RelayExpiry, RelayKeyId, RelaySignature,
        TerminalRawMessageSha256, TerminalOriginChainSha256, TerminalRelayChainSha256,
        TerminalId, TerminalClassification, TerminalTimestamp, TerminalKeyId, TerminalSignature,
        ReplayTerminalRecordSha256, ReplayOriginalChainSha256,
        ReplayId, ReplayAuditId, ReplayAuthorizedAt, ReplayExpiry, ReplayDestination, ReplayKeyId, ReplaySignature
    ];
}

public sealed class MessagingSecurityException : Exception
{
    public const string InvalidContext = "The message security context is invalid.";
    public const string UnsupportedContract = "The message contract is not approved.";
    public const string UntrustedProducer = "The message producer is not trusted.";
    public const string InvalidSignature = "The message signature is invalid.";
    public const string TenantMismatch = "The message tenant context is invalid.";
    public const string UntrustedCustody = "The message custody chain is invalid.";
    public const string ReplayRejected = "The protected replay request was rejected.";

    public MessagingSecurityException(string message) : base(message) { }
}

public interface ITenantWrightMessageSigner
{
    string Actor { get; }
    string KeyId { get; }
    byte[] Sign(ReadOnlySpan<byte> canonicalInput);
}

public sealed class TenantWrightMessageSigner : ITenantWrightMessageSigner, IDisposable
{
    private readonly ECDsa _key;

    public TenantWrightMessageSigner(IOptions<MessagingOptions> options)
    {
        var value = options.Value;
        Actor = value.ProducerPrincipal;
        KeyId = value.Trust.ActiveKeyId;
        var path = MessagingKeyFile.Validate(value.Trust.PrivateKeyPath, "private signing key");
        var pem = File.ReadAllText(path);
        try
        {
            _key = ECDsa.Create();
            _key.ImportFromPem(pem);
            if (_key.KeySize != 256)
                throw new InvalidOperationException("The private signing key must use ECDSA P-256.");
        }
        finally
        {
            pem = string.Empty;
        }
    }

    public string Actor { get; }
    public string KeyId { get; }
    public byte[] Sign(ReadOnlySpan<byte> canonicalInput) => _key.SignData(canonicalInput, HashAlgorithmName.SHA256);
    public void Dispose() => _key.Dispose();
}

public interface ITenantWrightPublicKeyRegistry
{
    bool Verify(string keyId, string actor, ReadOnlySpan<byte> canonicalInput, ReadOnlySpan<byte> signature);
}

public sealed class TenantWrightPublicKeyRegistry : ITenantWrightPublicKeyRegistry, IDisposable
{
    private readonly IReadOnlyDictionary<string, Entry> _entries;

    public TenantWrightPublicKeyRegistry(IOptions<MessagingOptions> options)
    {
        _entries = options.Value.Trust.PublicKeys.ToDictionary(item => item.KeyId, item => Load(item), StringComparer.Ordinal);
    }

    public bool Verify(string keyId, string actor, ReadOnlySpan<byte> canonicalInput, ReadOnlySpan<byte> signature)
    {
        if (!_entries.TryGetValue(keyId, out var entry) || entry.Revoked || !string.Equals(entry.Actor, actor, StringComparison.Ordinal))
            return false;
        return entry.Key.VerifyData(canonicalInput, signature, HashAlgorithmName.SHA256);
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values) entry.Key.Dispose();
    }

    private static Entry Load(MessagingPublicKeyOptions options)
    {
        var pem = File.ReadAllText(MessagingKeyFile.Validate(options.PublicKeyPath, "public verification key"));
        try
        {
            var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.KeySize != 256)
            {
                key.Dispose();
                throw new InvalidOperationException("Public verification keys must use ECDSA P-256.");
            }
            return new Entry(options.Actor, options.Revoked, key);
        }
        finally
        {
            pem = string.Empty;
        }
    }

    private sealed record Entry(string Actor, bool Revoked, ECDsa Key);
}

internal static class MessagingKeyFile
{
    public static string Validate(string configuredPath, string description)
    {
        if (!Path.IsPathFullyQualified(configuredPath))
            throw new InvalidOperationException($"The {description} path must be absolute.");
        var path = Path.GetFullPath(configuredPath);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException($"The {description} path must be a regular non-reparse file.");
        var readOnly = OperatingSystem.IsWindows()
            ? (attributes & FileAttributes.ReadOnly) != 0
            : (File.GetUnixFileMode(path) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        if (!readOnly)
            throw new InvalidOperationException($"The {description} file must be read-only.");
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return path;
    }
}

internal static class CanonicalAttestationInput
{
    public static byte[] Create(string domain, params string[] fields)
    {
        using var stream = new MemoryStream();
        Write(stream, domain);
        foreach (var field in fields) Write(stream, field);
        return stream.ToArray();
    }

    private static void Write(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
}
