using Shared.AuditLogs.Auditing;
using Shared.Domain;
using Shared.Domain.Entities;

namespace AuditLogs.Domain.Auditing;

public class EntityPropertyChange : Entity<long>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public long EntityChangeId { get; private set; }

    public string? NewValue { get; private set; }

    public string? OriginalValue { get; private set; }

    public string PropertyName { get; private set; } = default!;

    public string? PropertyTypeFullName { get; private set; }

    protected EntityPropertyChange()
    {
    }

    public EntityPropertyChange(
        long id,
        long entityChangeId,
        Guid? tenantId,
        string propertyName,
        string? propertyTypeFullName,
        string? originalValue,
        string? newValue)
        : base(id)
    {
        EntityChangeId = entityChangeId;
        TenantId = tenantId;
        PropertyName = global::Shared.Check.NotNullOrWhiteSpace(propertyName, nameof(propertyName), AuditLogConsts.MaxPropertyNameLength);
        PropertyTypeFullName = NormalizeOptional(propertyTypeFullName, nameof(propertyTypeFullName), AuditLogConsts.MaxPropertyTypeFullNameLength);
        OriginalValue = NormalizeOptional(originalValue, nameof(originalValue), AuditLogConsts.MaxOriginalValueLength);
        NewValue = NormalizeOptional(newValue, nameof(newValue), AuditLogConsts.MaxNewValueLength);
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
