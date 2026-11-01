using Shared.Domain;
using Shared.Domain.Entities;

namespace Notifications.Domain.Notifications;

public sealed class NotificationOperationReceipt : Entity<Guid>, IMultiTenant
{
    private NotificationOperationReceipt()
    {
    }

    public NotificationOperationReceipt(
        Guid id,
        Guid? tenantId,
        string scopeKey,
        string actorUserId,
        string idempotencyKey,
        string operation,
        string targetKey,
        string requestHash,
        long resultingRevision,
        int affectedCount,
        string responseJson,
        DateTime appliedAtUtc) : base(id)
    {
        TenantId = tenantId;
        ScopeKey = Required(scopeKey, nameof(scopeKey), 64);
        ActorUserId = Required(actorUserId, nameof(actorUserId), 128);
        IdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey), 128);
        Operation = Required(operation, nameof(operation), 64);
        TargetKey = Required(targetKey, nameof(targetKey), 128);
        RequestHash = Required(requestHash, nameof(requestHash), 64);
        ArgumentOutOfRangeException.ThrowIfNegative(resultingRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(affectedCount);
        ResultingRevision = resultingRevision;
        AffectedCount = affectedCount;
        ResponseJson = Required(responseJson, nameof(responseJson), 16_384);
        AppliedAtUtc = appliedAtUtc.Kind == DateTimeKind.Utc
            ? appliedAtUtc
            : throw new ArgumentException("Applied time must be UTC.", nameof(appliedAtUtc));
    }

    public Guid? TenantId { get; private set; }
    public string ScopeKey { get; private set; } = string.Empty;
    public string ActorUserId { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string Operation { get; private set; } = string.Empty;
    public string TargetKey { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public long ResultingRevision { get; private set; }
    public int AffectedCount { get; private set; }
    public string ResponseJson { get; private set; } = string.Empty;
    public DateTime AppliedAtUtc { get; private set; }

    private static string Required(string value, string parameterName, int maxLength)
    {
        var normalized = global::Shared.Check.NotNullOrWhiteSpace(value, parameterName, maxLength: maxLength).Trim();
        return normalized;
    }
}
