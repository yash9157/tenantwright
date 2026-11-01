using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;

namespace Shared.AuditLogs.Publishing;

public sealed class AuditLogEventSigner : ITransientDependency
{
    private readonly IOptionsMonitor<AuditLogSignatureOptions> _options;

    public AuditLogEventSigner(IOptionsMonitor<AuditLogSignatureOptions> options)
    {
        _options = options;
    }

    public bool TrySign(AuditLogWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.CurrentKeyId))
        {
            return false;
        }

        var keyId = options.CurrentKeyId.Trim();
        var key = ResolveKey(options, keyId);
        var timestamp = DateTime.UtcNow;
        request.SignatureKeyId = keyId;
        request.SignatureTimestampUtc = timestamp;
        request.Signature = ComputeSignature(AuditLogSignaturePayload.FromRequest(request), key, timestamp, keyId);
        return true;
    }

    public void Validate(AuditLogWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(request.SignatureKeyId) ||
            string.IsNullOrWhiteSpace(request.Signature) ||
            !request.SignatureTimestampUtc.HasValue)
        {
            if (options.RequireSignatures)
            {
                throw new InvalidOperationException("Audit log event signature is required.");
            }

            return;
        }

        var now = DateTime.UtcNow;
        var tolerance = options.FreshnessTolerance <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(5)
            : options.FreshnessTolerance;
        if (request.SignatureTimestampUtc.Value < now.Subtract(tolerance) ||
            request.SignatureTimestampUtc.Value > now.Add(tolerance))
        {
            throw new InvalidOperationException("Audit log event signature timestamp is outside the allowed freshness window.");
        }

        var keyId = request.SignatureKeyId.Trim();
        var key = ResolveKey(options, keyId);
        var expected = ComputeSignature(
            AuditLogSignaturePayload.FromRequest(request),
            key,
            request.SignatureTimestampUtc.Value,
            keyId);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(request.Signature.Trim())))
        {
            throw new InvalidOperationException("Audit log event signature is invalid.");
        }
    }

    /// <summary>
    /// Recomputes the HMAC over a persisted row's canonical fields and compares it against the stored
    /// signature. Used to detect at-rest modification (sub-flow 1a). Unlike <see cref="Validate"/>, this
    /// performs no freshness check since persisted rows may be arbitrarily old.
    /// </summary>
    public bool? VerifyPersisted(
        AuditLogSignaturePayload payload,
        string? signatureKeyId,
        DateTime? signatureTimestampUtc,
        string? signature)
    {
        if (string.IsNullOrWhiteSpace(signatureKeyId) ||
            string.IsNullOrWhiteSpace(signature) ||
            !signatureTimestampUtc.HasValue)
        {
            // No signature was captured for this row (e.g. written before 1a, or signing disabled).
            return null;
        }

        var options = _options.CurrentValue;
        byte[] resolvedKey;
        try
        {
            resolvedKey = ResolveKey(options, signatureKeyId.Trim());
        }
        catch (InvalidOperationException)
        {
            // Signing key no longer configured/rotated out - can't verify, but don't crash the read path.
            return null;
        }

        try
        {
            var expected = ComputeSignature(payload, resolvedKey, signatureTimestampUtc.Value, signatureKeyId.Trim());
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(signature.Trim()));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ComputeSignature(
        AuditLogSignaturePayload payload,
        byte[] key,
        DateTime timestamp,
        string keyId)
    {
        var canonical = string.Join(
            "\n",
            payload.EventId.ToString("D"),
            payload.TenantId?.ToString("D") ?? string.Empty,
            payload.OccurredAtUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            payload.SourceService,
            payload.Category,
            payload.UserId ?? string.Empty,
            payload.CorrelationId ?? string.Empty,
            payload.Path ?? string.Empty,
            payload.PayloadBytes.ToString(CultureInfo.InvariantCulture),
            payload.RedactionApplied ? "1" : "0",
            payload.RedactionVersion.ToString(CultureInfo.InvariantCulture),
            keyId,
            timestamp.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));

        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(signature);
    }

    private static byte[] ResolveKey(AuditLogSignatureOptions options, string keyId)
    {
        if (!options.Keys.TryGetValue(keyId, out var encodedKey) || string.IsNullOrWhiteSpace(encodedKey))
        {
            throw new InvalidOperationException($"Audit signature key '{keyId}' is not configured.");
        }

        var key = Convert.FromBase64String(encodedKey.Trim());
        if (key.Length < 32)
        {
            throw new InvalidOperationException($"Audit signature key '{keyId}' must be at least 32 bytes.");
        }

        return key;
    }
}

/// <summary>
/// The subset of an audit event's canonical fields that participate in HMAC signing. Shared between
/// ingest-time signing (from <see cref="AuditLogWriteRequest"/>) and read/export-time re-verification
/// (from a persisted <c>AuditLog</c> row), so both sides compute the exact same canonical string.
/// </summary>
public sealed record AuditLogSignaturePayload(
    Guid EventId,
    Guid? TenantId,
    DateTime OccurredAtUtc,
    string SourceService,
    string Category,
    string? UserId,
    string? CorrelationId,
    string? Path,
    int PayloadBytes,
    bool RedactionApplied,
    int RedactionVersion)
{
    public static AuditLogSignaturePayload FromRequest(AuditLogWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = request.TenantId == Guid.Empty ? null : request.TenantId;

        return new AuditLogSignaturePayload(
            request.EventId,
            tenantId,
            request.OccurredAtUtc,
            request.SourceService ?? string.Empty,
            request.Category,
            request.UserId,
            request.CorrelationId,
            request.Path,
            request.PayloadBytes,
            request.RedactionApplied,
            request.RedactionVersion);
    }
}
