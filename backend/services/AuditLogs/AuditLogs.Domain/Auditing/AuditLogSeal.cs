using AuditLogs.Domain.Shared.Auditing;
using Shared.Domain.Entities.Auditing;

namespace AuditLogs.Domain.Auditing;

/// <summary>
/// Append-only chained seal over a contiguous batch of <see cref="AuditLog"/> rows, keyed by
/// <c>Id</c> range. Each seal signs a hash of the batch plus the previous seal's hash, so deleting or
/// reordering any sealed row breaks the chain on the next verification pass (sub-flow 1b: at-rest
/// tamper-evidence, deletion/reordering detection). Complements <see cref="AuditLog.Signature"/>, which
/// only detects modification of a single row in isolation.
/// </summary>
public class AuditLogSeal : CreationAuditedAggregateRoot<long>
{
    /// <summary>
    /// First <see cref="AuditLog.Id"/> (inclusive) covered by this seal.
    /// </summary>
    public long FromId { get; private set; }

    /// <summary>
    /// Last <see cref="AuditLog.Id"/> (inclusive) covered by this seal.
    /// </summary>
    public long ToId { get; private set; }

    /// <summary>
    /// Row count actually included in the batch hash (may be less than <c>ToId - FromId + 1</c> if some
    /// ids in range don't exist, e.g. never used due to identity gaps - this is still valid since the
    /// hash is computed over the rows found, not the id range).
    /// </summary>
    public long RowCount { get; private set; }

    /// <summary>
    /// Hex-encoded SHA-256 hash of the previous seal's <see cref="Signature"/> (or a fixed genesis value
    /// for the very first seal). Makes the seals a hash chain: breaking/removing any prior seal or row
    /// invalidates every seal after it.
    /// </summary>
    public string PrevSealHash { get; private set; } = default!;

    /// <summary>
    /// Hex-encoded SHA-256 hash over the ordered (by Id) canonical fields of every row in
    /// [<see cref="FromId"/>, <see cref="ToId"/>].
    /// </summary>
    public string BatchHash { get; private set; } = default!;

    /// <summary>
    /// Hex-encoded HMAC over <see cref="PrevSealHash"/> + <see cref="BatchHash"/> + range, using the
    /// same keyring as row-level signatures (<c>AuditLogSignatureOptions</c>).
    /// </summary>
    public string Signature { get; private set; } = default!;

    public string SignatureKeyId { get; private set; } = default!;

    protected AuditLogSeal()
    {
    }

    public AuditLogSeal(
        long id,
        long fromId,
        long toId,
        long rowCount,
        string prevSealHash,
        string batchHash,
        string signature,
        string signatureKeyId)
        : base(id)
    {
        if (toId < fromId)
        {
            throw new ArgumentException("toId must be greater than or equal to fromId.", nameof(toId));
        }

        FromId = fromId;
        ToId = toId;
        RowCount = Math.Max(0, rowCount);
        PrevSealHash = global::Shared.Check.NotNullOrWhiteSpace(
            prevSealHash, nameof(prevSealHash), AuditLogSealConsts.MaxHashLength);
        BatchHash = global::Shared.Check.NotNullOrWhiteSpace(
            batchHash, nameof(batchHash), AuditLogSealConsts.MaxHashLength);
        Signature = global::Shared.Check.NotNullOrWhiteSpace(
            signature, nameof(signature), AuditLogSealConsts.MaxSignatureLength);
        SignatureKeyId = global::Shared.Check.NotNullOrWhiteSpace(
            signatureKeyId, nameof(signatureKeyId), AuditLogSealConsts.MaxSignatureKeyIdLength);
    }
}
