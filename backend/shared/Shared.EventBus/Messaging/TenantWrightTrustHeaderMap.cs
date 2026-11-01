using System.Globalization;

namespace Shared.EventBus.Messaging;

/// <summary>
/// Transport-neutral projection between attestations and the <see cref="TenantWrightTrustHeaders"/> wire names. Every
/// provider writes and reads exactly these names and value formats.
/// </summary>
public static class TenantWrightTrustHeaderMap
{
    private const string InstantFormat = "O";

    /// <summary>The origin headers for <paramref name="origin"/>. Carries no secret: only signed, public custody.</summary>
    public static IReadOnlyDictionary<string, string> FromOrigin(OriginAttestation origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TenantWrightTrustHeaders.ContextVersion] = "1",
            [TenantWrightTrustHeaders.Producer] = origin.OriginPrincipal,
            [TenantWrightTrustHeaders.Scope] = origin.Scope,
            [TenantWrightTrustHeaders.OriginKeyId] = origin.KeyId,
            [TenantWrightTrustHeaders.OriginIssuedAt] = FormatInstant(origin.IssuedAt),
            [TenantWrightTrustHeaders.OriginPayloadSha256] = origin.PayloadSha256,
            [TenantWrightTrustHeaders.OriginSignature] = origin.Signature
        };
        if (origin.TenantId is not null)
            headers[TenantWrightTrustHeaders.TenantId] = origin.TenantId;
        return headers;
    }

    public static IReadOnlyDictionary<string, string> FromRelay(RelayAttestation relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TenantWrightTrustHeaders.RelayId] = relay.RelayId,
            [TenantWrightTrustHeaders.RelayAttempt] = relay.Attempt.ToString(CultureInfo.InvariantCulture),
            [TenantWrightTrustHeaders.RelayNotBefore] = FormatInstant(relay.NotBefore),
            [TenantWrightTrustHeaders.RelayExpiry] = FormatInstant(relay.ExpiresAt),
            [TenantWrightTrustHeaders.RelayKeyId] = relay.KeyId,
            [TenantWrightTrustHeaders.RelaySignature] = relay.Signature
        };
    }

    public static IReadOnlyDictionary<string, string> FromReplay(ReplayAttestation replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TenantWrightTrustHeaders.ReplayTerminalRecordSha256] = replay.TerminalRecordSha256,
            [TenantWrightTrustHeaders.ReplayOriginalChainSha256] = replay.OriginalChainSha256,
            [TenantWrightTrustHeaders.ReplayId] = replay.ReplayId,
            [TenantWrightTrustHeaders.ReplayAuditId] = replay.AuditId,
            [TenantWrightTrustHeaders.ReplayAuthorizedAt] = FormatInstant(replay.AuthorizedAt),
            [TenantWrightTrustHeaders.ReplayExpiry] = FormatInstant(replay.ExpiresAt),
            [TenantWrightTrustHeaders.ReplayDestination] = replay.Destination,
            [TenantWrightTrustHeaders.ReplayKeyId] = replay.KeyId,
            [TenantWrightTrustHeaders.ReplaySignature] = replay.Signature
        };
    }

    /// <summary>Reads the origin attestation of a message from its trust headers. Fails closed on any missing field.</summary>
    public static OriginAttestation ReadOrigin(
        IReadOnlyDictionary<string, string?> headers,
        Guid messageId,
        string contractIdentity)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!string.Equals(Required(headers, TenantWrightTrustHeaders.ContextVersion), "1", StringComparison.Ordinal))
            throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);
        return new OriginAttestation(
            messageId,
            contractIdentity,
            Required(headers, TenantWrightTrustHeaders.Producer),
            Required(headers, TenantWrightTrustHeaders.Scope),
            headers.GetValueOrDefault(TenantWrightTrustHeaders.TenantId),
            ParseInstant(headers, TenantWrightTrustHeaders.OriginIssuedAt),
            Required(headers, TenantWrightTrustHeaders.OriginPayloadSha256),
            Required(headers, TenantWrightTrustHeaders.OriginKeyId),
            Required(headers, TenantWrightTrustHeaders.OriginSignature));
    }

    public static string Required(IReadOnlyDictionary<string, string?> headers, string key) =>
        headers.TryGetValue(key, out var value) && value is not null
            ? value
            : throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);

    public static DateTimeOffset ParseInstant(IReadOnlyDictionary<string, string?> headers, string key) =>
        DateTimeOffset.ParseExact(
            Required(headers, key),
            InstantFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static string FormatInstant(DateTimeOffset value) =>
        value.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);
}
