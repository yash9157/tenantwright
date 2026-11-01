using Shared.AuditLogs.Auditing;
using Shared.Domain;
using Shared.Domain.Entities;

namespace AuditLogs.Domain.Auditing;

public class AuditLogAction : Entity<long>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public long AuditLogId { get; private set; }

    public string ServiceName { get; private set; } = default!;

    public string MethodName { get; private set; } = default!;

    public string Parameters { get; private set; } = default!;

    public DateTime ExecutionTime { get; private set; }

    public int ExecutionDuration { get; private set; }

    public string? Result { get; private set; }

    protected AuditLogAction()
    {
    }

    public AuditLogAction(
        long id,
        long auditLogId,
        Guid? tenantId,
        string serviceName,
        string methodName,
        string parameters,
        DateTime executionTime,
        int executionDuration,
        string? result)
        : base(id)
    {
        AuditLogId = auditLogId;
        TenantId = tenantId;
        ServiceName = global::Shared.Check.NotNullOrWhiteSpace(serviceName, nameof(serviceName), AuditLogConsts.MaxServiceNameLength);
        MethodName = global::Shared.Check.NotNullOrWhiteSpace(methodName, nameof(methodName), AuditLogConsts.MaxMethodNameLength);
        Parameters = NormalizeOptional(parameters, nameof(parameters), AuditLogConsts.MaxParametersLength) ?? "{}";
        ExecutionTime = NormalizeUtc(executionTime);
        ExecutionDuration = Math.Max(0, executionDuration);
        Result = NormalizeOptional(result, nameof(result), AuditLogConsts.MaxActionResultLength);
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
