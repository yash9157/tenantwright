using System.Security.Cryptography;
using System.Text;
using AuditLogs.Domain.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.Publishing;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using Shared.EntityFrameworkCore;
namespace AuditLogs.Application.Auditing;

/// <summary>
/// Builds the append-only <see cref="AuditLogSeal"/> hash chain (sub-flow 1b). Runs host-context only
/// (audit logs are not tenant-partitioned for sealing purposes - the chain covers all tenants' rows in
/// a single global Id sequence, matching how <c>AuditLog.Id</c> itself is a single global identity).
/// </summary>
public sealed class AuditLogSealService : ITransientDependency
{
    private readonly IDbContextProvider<AuditLogsDbContextApp> _dbContextProvider;
    private readonly IUnitOfWorkExecutor _uowExecutor;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDataFilter _dataFilter;
    private readonly IOptionsMonitor<AuditLogSealOptions> _sealOptions;
    private readonly IOptionsMonitor<AuditLogSignatureOptions> _signatureOptions;
    private readonly ILogger<AuditLogSealService> _logger;

    public AuditLogSealService(
        IDbContextProvider<AuditLogsDbContextApp> dbContextProvider, IUnitOfWorkExecutor uowExecutor, ICurrentTenant currentTenant, IDataFilter dataFilter, IOptionsMonitor<AuditLogSealOptions> sealOptions, IOptionsMonitor<AuditLogSignatureOptions> signatureOptions, ILogger<AuditLogSealService> logger)
    {
        _dbContextProvider = dbContextProvider;
        _uowExecutor = uowExecutor;
        _currentTenant = currentTenant;
        _dataFilter = dataFilter;
        _sealOptions = sealOptions;
        _signatureOptions = signatureOptions;
        _logger = logger;
    }

    /// <summary>
    /// Seals one batch of newly-eligible rows, if any. Returns true if a seal was created.
    /// </summary>
    public async Task<bool> SealNextBatchAsync(CancellationToken cancellationToken = default)
        => await _uowExecutor.ExecuteNonTransactionalAsync(
            _ => SealNextBatchCoreAsync(cancellationToken),
            cancellationToken);

    private async Task<bool> SealNextBatchCoreAsync(CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var options = _sealOptions.CurrentValue;
        if (!options.Enabled)
        {
            return false;
        }

        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var lastSeal = await FindTailSealAsync(cancellationToken);
            var fromId = (lastSeal?.ToId ?? 0) + 1;

            var cutoffUtc = DateTime.UtcNow.AddSeconds(-options.WatermarkLagSeconds);
            var windowEndId = fromId + options.MaxBatchSize - 1;

            // Load every existing row in the candidate window, in Id order. We deliberately do NOT
            // just take MAX(Id) among old-enough rows: a naive "seal up to the newest old-enough row"
            // can leapfrog over a lower Id whose insert transaction started earlier but is still
            // in-flight (or was slower to commit) - see the walk below for why that matters.
            var existingRows = await unitOfWorkDbContext.AuditLogs
                .AsNoTracking()
                .Where(x => x.Id >= fromId && x.Id <= windowEndId)
                .OrderBy(x => x.Id)
                .Select(x => new SealRowProjection
                {
                    Id = x.Id,
                    EventId = x.EventId,
                    TenantId = x.TenantId,
                    ExecutionTime = x.ExecutionTime,
                    CreationTime = x.CreationTime,
                    ApplicationName = x.ApplicationName,
                    Category = x.Category,
                    Signature = x.Signature
                })
                .ToListAsync(cancellationToken);

            var byId = existingRows.ToDictionary(x => x.Id);

            // Walk Id-by-Id from fromId. This is the watermark: a lower Id whose row doesn't exist yet
            // is either (a) a transaction still in flight - in which case we stop here and wait for it
            // to either commit (it'll be picked up on a later run without ever being skipped) or
            // (b) a rolled-back transaction that will NEVER produce a row - a permanent gap. We can only
            // tell these apart by how much time has passed: if a LATER Id already exists and is itself
            // old enough (past the watermark), then whatever held the earlier Id has had at least that
            // much time to commit and didn't, so we treat it as abandoned and skip over it. If no later
            // row is old enough yet, we can't tell (a) from (b) yet, so we stop and wait.
            long? toId = null;
            var cursor = fromId;
            while (cursor <= windowEndId)
            {
                if (byId.TryGetValue(cursor, out var row))
                {
                    if (row.CreationTime > cutoffUtc && !IsGapAbandoned(byId, cursor, windowEndId, cutoffUtc))
                    {
                        // This row itself isn't old enough yet, and nothing later proves it's safe to
                        // include, so stop the batch here.
                        break;
                    }

                    toId = cursor;
                    cursor++;
                    continue;
                }

                // Gap at `cursor`. Only skip past it if some later Id in the window already exists and
                // is old enough to prove the gap is abandoned, not just in-flight.
                if (!IsGapAbandoned(byId, cursor, windowEndId, cutoffUtc))
                {
                    break;
                }

                toId = cursor;
                cursor++;
            }

            if (!toId.HasValue)
            {
                // Nothing eligible yet - either no new rows, or the very next row/gap isn't provably
                // safe to include.
                return false;
            }

            var cappedToId = toId.Value;
            var rows = existingRows.Where(x => x.Id <= cappedToId).ToList();

            var batchHash = ComputeBatchHash(fromId, cappedToId, rows);
            var prevSealHash = lastSeal == null
                ? AuditLogSealConsts.GenesisHash
                : ComputeSealHash(lastSeal);

            var options2 = _signatureOptions.CurrentValue;
            if (string.IsNullOrWhiteSpace(options2.CurrentKeyId))
            {
                _logger.LogWarning("Audit log sealing is enabled but no signing key is configured (AuditLogging:Signatures:CurrentKeyId); skipping this run.");
                return false;
            }

            var keyId = options2.CurrentKeyId.Trim();
            var key = ResolveKey(options2, keyId);
            var signature = ComputeSealSignature(prevSealHash, batchHash, fromId, cappedToId, key, keyId);

            var seal = new AuditLogSeal(
                id: 0,
                fromId: fromId,
                toId: cappedToId,
                rowCount: rows.Count,
                prevSealHash: prevSealHash,
                batchHash: batchHash,
                signature: signature,
                signatureKeyId: keyId);

            // Only the append-only seal insertion is transactional. Row loading, hash calculation, and
            // signature generation above stay outside any database transaction. Completion of the fresh
            // intercepted unit of work owns the transaction and commit. The insert flushes inside that
            // transaction so persistence errors are raised before the delegate returns.
            //
            // The seal is written through the scope's DbContext, never a repository: AuditLogSeal is not
            // tenant-owned, and repositories route such entities to the shared database, while the chain must
            // live in the database whose rows it covers (the shared one, or a tenant's dedicated one).
            await _uowExecutor.ExecuteTransactionalAsync(
                async ct =>
                {
                    var sealDbContext = await _dbContextProvider.GetDbContextAsync();
                    sealDbContext.Set<AuditLogSeal>().Add(seal);
                    await sealDbContext.SaveChangesAsync(ct);
                },
                cancellationToken);

            return true;
        });
    }

    /// <summary>
    /// Re-verifies row signatures and walks the seal chain over [fromId, toId], reporting any tampering
    /// (modified rows, missing rows within a sealed range, or broken chain links).
    /// </summary>
    public async Task<AuditLogSealVerificationResult> VerifyRangeAsync(
        long fromId,
        long toId,
        AuditLogEventSigner rowSigner,
        CancellationToken cancellationToken = default)
        => await _uowExecutor.ExecuteNonTransactionalAsync(
            _ => VerifyRangeCoreAsync(fromId, toId, rowSigner, cancellationToken),
            cancellationToken);

    private async Task<AuditLogSealVerificationResult> VerifyRangeCoreAsync(
        long fromId,
        long toId,
        AuditLogEventSigner rowSigner,
        CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (toId < fromId)
        {
            throw new ArgumentException("toId must be greater than or equal to fromId.", nameof(toId));
        }

        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var seals = await unitOfWorkDbContext.Set<AuditLogSeal>()
                .AsNoTracking()
                .Where(x => x.ToId >= fromId && x.FromId <= toId)
                .OrderBy(x => x.FromId)
                .ToListAsync(cancellationToken);

            var result = new AuditLogSealVerificationResult { FromId = fromId, ToId = toId };
            if (seals.Count == 0)
            {
                result.Issues.Add("No seals cover this range yet (rows may be unsealed, or the range is invalid).");
                return result;
            }

            // Chain continuity: each seal (after the first examined) must start exactly where the
            // previous one ended, and its PrevSealHash must match the hash of the seal before it.
            AuditLogSeal? previous = null;
            foreach (var seal in seals)
            {
                if (previous != null)
                {
                    if (seal.FromId != previous.ToId + 1)
                    {
                        result.Issues.Add(
                            $"Gap or overlap between seals: seal ending at {previous.ToId} is followed by seal starting at {seal.FromId}.");
                        result.ChainIntact = false;
                    }

                    var expectedPrevHash = ComputeSealHash(previous);
                    if (!string.Equals(seal.PrevSealHash, expectedPrevHash, StringComparison.Ordinal))
                    {
                        result.Issues.Add(
                            $"Seal chain broken between range [{previous.FromId},{previous.ToId}] and [{seal.FromId},{seal.ToId}]: PrevSealHash does not match.");
                        result.ChainIntact = false;
                    }
                }

                var fullRows = await unitOfWorkDbContext.AuditLogs
                    .AsNoTracking()
                    .Where(x => x.Id >= seal.FromId && x.Id <= seal.ToId)
                    .OrderBy(x => x.Id)
                    .ToListAsync(cancellationToken);

                var rows = fullRows
                    .Select(x => new SealRowProjection
                    {
                        Id = x.Id,
                        EventId = x.EventId,
                        TenantId = x.TenantId,
                        ExecutionTime = x.ExecutionTime,
                        ApplicationName = x.ApplicationName,
                        Category = x.Category,
                        Signature = x.Signature
                    })
                    .ToList();

                if (rows.Count != seal.RowCount)
                {
                    result.Issues.Add(
                        $"Row count mismatch for sealed range [{seal.FromId},{seal.ToId}]: expected {seal.RowCount}, found {rows.Count} (possible deletion).");
                    result.ChainIntact = false;
                }

                var recomputedBatchHash = ComputeBatchHash(seal.FromId, seal.ToId, rows);
                if (!string.Equals(recomputedBatchHash, seal.BatchHash, StringComparison.Ordinal))
                {
                    result.Issues.Add(
                        $"Batch hash mismatch for sealed range [{seal.FromId},{seal.ToId}]: row contents changed since sealing.");
                    result.ChainIntact = false;
                }

                foreach (var row in fullRows)
                {
                    var payload = new AuditLogSignaturePayload(
                        row.EventId,
                        row.TenantId,
                        row.ExecutionTime,
                        row.ApplicationName,
                        row.Category,
                        row.UserId,
                        row.CorrelationId,
                        row.Url,
                        ParseIntExtraProperty(row.ExtraProperties, AuditLogs.Domain.Shared.Auditing.AuditLogExtraPropertyNames.PayloadBytes) ?? 0,
                        ParseBoolExtraProperty(row.ExtraProperties, AuditLogs.Domain.Shared.Auditing.AuditLogExtraPropertyNames.RedactionApplied),
                        ParseIntExtraProperty(row.ExtraProperties, AuditLogs.Domain.Shared.Auditing.AuditLogExtraPropertyNames.RedactionVersion) ?? 0);
                    var verified = rowSigner.VerifyPersisted(payload, row.SignatureKeyId, row.SignatureTimestampUtc, row.Signature);
                    if (verified == false)
                    {
                        result.Issues.Add($"Row {row.Id} signature does not match its persisted fields (tampered).");
                        result.ChainIntact = false;
                    }
                }

                result.RowsChecked += rows.Count;
                previous = seal;
            }

            result.SealsChecked = seals.Count;
            return result;
        });
    }

    /// <summary>
    /// True if some row with Id greater than <paramref name="id"/> (within the batch window) already
    /// exists and is itself old enough to be past the watermark. If so, whatever would have produced a
    /// row at <paramref name="id"/> has had at least as much time to commit (Postgres identity values
    /// are assigned in increasing order as transactions commit) and didn't, so it's safe to treat
    /// <paramref name="id"/> as a permanent gap (rolled-back insert) rather than an in-flight write.
    /// </summary>
    private static bool IsGapAbandoned(
        Dictionary<long, SealRowProjection> byId,
        long id,
        long windowEndId,
        DateTime cutoffUtc)
    {
        for (var laterId = id + 1; laterId <= windowEndId; laterId++)
        {
            if (byId.TryGetValue(laterId, out var laterRow) && laterRow.CreationTime <= cutoffUtc)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<AuditLogSeal?> FindTailSealAsync(CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        return await unitOfWorkDbContext.Set<AuditLogSeal>()
                    .AsNoTracking()
                    .OrderByDescending(x => x.ToId)
                    .FirstOrDefaultAsync(cancellationToken);
    }

    // Per database: sealing runs once per AuditLogs database through the seal worker's fan-out (each database chains its
    // own Id sequence, including any rows a moved tenant left in the shared one); verification ranges target the
    // shared database's chain (see AuditLogSealVerificationBackgroundJob).
    private async Task<T> ExecuteWithHostMultiTenantFilterDisabledAsync<T>(Func<Task<T>> action)
    {
        if (_currentTenant.Id.HasValue)
        {
            return await action();
        }

        using (_dataFilter.Disable<IMultiTenant>())
        {
            return await action();
        }
    }

    internal static string ComputeBatchHash(long fromId, long toId, IReadOnlyList<SealRowProjection> rows)
    {
        var builder = new StringBuilder();
        builder.Append(fromId).Append('|').Append(toId).Append('\n');
        foreach (var row in rows.OrderBy(x => x.Id))
        {
            builder
                .Append(row.Id).Append('|')
                .Append(row.EventId.ToString("D")).Append('|')
                .Append(row.TenantId?.ToString("D") ?? string.Empty).Append('|')
                .Append(row.ExecutionTime.ToUniversalTime().Ticks).Append('|')
                .Append(row.ApplicationName).Append('|')
                .Append(row.Category).Append('|')
                .Append(row.Signature ?? string.Empty)
                .Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    internal static string ComputeSealHash(AuditLogSeal seal)
    {
        var canonical = string.Join(
            "\n",
            seal.FromId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            seal.ToId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            seal.PrevSealHash,
            seal.BatchHash,
            seal.Signature);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash);
    }

    private static string ComputeSealSignature(
        string prevSealHash,
        string batchHash,
        long fromId,
        long toId,
        byte[] key,
        string keyId)
    {
        var canonical = string.Join(
            "\n",
            fromId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            toId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            prevSealHash,
            batchHash,
            keyId);
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

    private static int? ParseIntExtraProperty(ExtraPropertyDictionary extraProperties, string key)
    {
        if (!extraProperties.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static bool ParseBoolExtraProperty(ExtraPropertyDictionary extraProperties, string key)
    {
        if (!extraProperties.TryGetValue(key, out var value) || value == null)
        {
            return false;
        }

        return bool.TryParse(value.ToString(), out var parsed) && parsed;
    }
}

internal sealed class SealRowProjection
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid? TenantId { get; set; }
    public DateTime ExecutionTime { get; set; }
    public DateTime CreationTime { get; set; }
    public string ApplicationName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Signature { get; set; }
}

public sealed class AuditLogSealVerificationResult
{
    public long FromId { get; set; }

    public long ToId { get; set; }

    public bool ChainIntact { get; set; } = true;

    public int SealsChecked { get; set; }

    public long RowsChecked { get; set; }

    public List<string> Issues { get; set; } = [];
}
