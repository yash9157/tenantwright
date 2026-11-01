using Shared.AuditLogs.Auditing;
using Shared.Domain;
using Shared.Domain.Entities;

namespace AuditLogs.Domain.Auditing;

public class EntityChange : Entity<long>, IMultiTenant
{
    public long AuditLogId { get; private set; }

    public Guid? TenantId { get; private set; }

    public DateTime ChangeTime { get; private set; }

    public string ChangeType { get; private set; } = default!;

    public Guid? EntityTenantId { get; private set; }

    public string EntityId { get; private set; } = default!;

    public string EntityTypeFullName { get; private set; } = default!;

    public virtual ICollection<EntityPropertyChange> PropertyChanges { get; protected set; } = [];

    protected EntityChange()
    {
    }

    public EntityChange(
        long id,
        long auditLogId,
        Guid? tenantId,
        DateTime changeTime,
        string changeType,
        Guid? entityTenantId,
        string entityId,
        string entityTypeFullName)
        : base(id)
    {
        AuditLogId = auditLogId;
        TenantId = tenantId;
        ChangeTime = NormalizeUtc(changeTime);
        ChangeType = global::Shared.Check.NotNullOrWhiteSpace(changeType, nameof(changeType), AuditLogConsts.MaxEntityChangeTypeLength);
        EntityTenantId = entityTenantId;
        EntityId = global::Shared.Check.NotNullOrWhiteSpace(entityId, nameof(entityId), AuditLogConsts.MaxEntityIdLength);
        EntityTypeFullName = global::Shared.Check.NotNullOrWhiteSpace(entityTypeFullName, nameof(entityTypeFullName), AuditLogConsts.MaxEntityTypeFullNameLength);
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
        {
            return DateTime.UtcNow;
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
