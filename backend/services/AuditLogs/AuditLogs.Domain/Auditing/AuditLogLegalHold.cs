using AuditLogs.Domain.Shared.Auditing;
using Shared.AuditLogs.Auditing;
using Shared.Domain.Entities.Auditing;

namespace AuditLogs.Domain.Auditing;

/// <summary>
/// Marks a scope of audit log rows as exempt from retention/archive/cleanup deletion (sub-flow 5:
/// legal hold). A hold is defined by an optional tenant, an optional date range, and/or an optional
/// subject (user id). Rows matching ALL specified dimensions of an active hold are protected -
/// unspecified dimensions (null) act as wildcards, so a hold with only <see cref="TenantId"/> set
/// covers that entire tenant, and a hold with no dimensions set at all would cover every row (blocked
/// by <see cref="AuditLogLegalHold(long, string, Guid?, DateTime?, DateTime?, string?)"/> validation -
/// at least one dimension is required to avoid an accidental global hold).
/// Ties into sub-flow 1b: held rows must remain sealed (a hold blocks *deletion*, not sealing) - the
/// sealing worker is unaffected by holds; only archive/export/cleanup deletion paths check them.
/// </summary>
public class AuditLogLegalHold : CreationAuditedAggregateRoot<long>
{
    public string Name { get; private set; } = default!;

    public Guid? TenantId { get; private set; }

    public DateTime? FromUtc { get; private set; }

    public DateTime? ToUtc { get; private set; }

    public string? UserId { get; private set; }

    public string? Reason { get; private set; }

    public bool IsReleased { get; private set; }

    public DateTime? ReleasedAtUtc { get; private set; }

    protected AuditLogLegalHold()
    {
    }

    public AuditLogLegalHold(
        long id,
        string name,
        Guid? tenantId,
        DateTime? fromUtc,
        DateTime? toUtc,
        string? userId,
        string? reason = null)
        : base(id)
    {
        Name = global::Shared.Check.NotNullOrWhiteSpace(name, nameof(name), AuditLogLegalHoldConsts.MaxNameLength);


        if (!tenantId.HasValue && !fromUtc.HasValue && !toUtc.HasValue && string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "A legal hold must scope to at least one of TenantId, a date range, or UserId to avoid an unbounded global hold.");
        }

        if (fromUtc.HasValue && toUtc.HasValue && toUtc.Value < fromUtc.Value)
        {
            throw new ArgumentException("ToUtc must be greater than or equal to FromUtc.", nameof(toUtc));
        }

        TenantId = tenantId;
        FromUtc = NormalizeUtc(fromUtc);
        ToUtc = NormalizeUtc(toUtc);
        UserId = NormalizeOptional(userId, nameof(userId), AuditLogConsts.MaxUserIdLength);
        Reason = NormalizeOptional(reason, nameof(reason), AuditLogLegalHoldConsts.MaxReasonLength);
        IsReleased = false;
    }

    public void Release(string? reason)
    {
        if (IsReleased)
        {
            return;
        }

        IsReleased = true;
        ReleasedAtUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            Reason = NormalizeOptional(reason, nameof(reason), AuditLogLegalHoldConsts.MaxReasonLength);
        }
    }

    /// <summary>
    /// True if this active hold covers the given row's dimensions. Each set dimension on the hold must
    /// match; unset dimensions are wildcards.
    /// </summary>
    public bool Covers(Guid? rowTenantId, DateTime rowExecutionTimeUtc, string? rowUserId)
    {
        if (IsReleased)
        {
            return false;
        }

        if (TenantId.HasValue && TenantId.Value != rowTenantId)
        {
            return false;
        }

        if (FromUtc.HasValue && rowExecutionTimeUtc < FromUtc.Value)
        {
            return false;
        }

        if (ToUtc.HasValue && rowExecutionTimeUtc > ToUtc.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(UserId) &&
            !string.Equals(UserId, rowUserId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue || value.Value == default)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private static string? NormalizeOptional(string? value, string paramName, int maxLength)
    {
        var normalized = value?.Trim();
        if (normalized is { Length: > 0 } && normalized.Length > maxLength)
        {
            throw new ArgumentException($"{paramName} length must be equal to or lower than {maxLength}.", paramName);
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
