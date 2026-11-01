using System.Reflection;

namespace Shared.AuditLogs.Auditing;

public interface IAuditingManager
{
    IAuditLogScope? Current { get; }

    IAuditLogSaveHandle BeginScope();
}

public interface IAuditLogScope
{
    AuditLogInfo Log { get; }
}

public interface IAuditLogSaveHandle : IDisposable
{
    Task SaveAsync();
}

public interface IAuditingStore
{
    Task SaveAsync(AuditLogInfo auditInfo);
}

public interface IAuditingHelper
{
    bool ShouldSaveAudit(
        MethodInfo? methodInfo,
        bool defaultValue = false,
        bool ignoreIntegrationServiceAttribute = false);

    bool IsEntityHistoryEnabled(Type entityType, bool defaultValue = false);

    AuditLogInfo CreateAuditLogInfo();

    AuditLogActionInfo CreateAuditLogAction(Type? type, MethodInfo method, object?[] arguments);

    AuditLogActionInfo CreateAuditLogAction(Type? type, MethodInfo method, IDictionary<string, object?> arguments);

    IDisposable DisableAuditing();

    bool IsAuditingEnabled();
}

public sealed class AuditLogScope : IAuditLogScope
{
    public AuditLogInfo Log { get; }

    public AuditLogScope(AuditLogInfo log)
    {
        Log = log;
    }
}

[Serializable]
public sealed class AuditLogInfo
{
    public string? ApplicationName { get; set; }

    public string Category { get; set; } = "http.request";

    public string? UserId { get; set; }

    public string? UserName { get; set; }

    public Guid? TenantId { get; set; }

    public string? TenantName { get; set; }

    public string? ImpersonatorUserId { get; set; }

    public Guid? ImpersonatorTenantId { get; set; }

    public string? ImpersonatorUserName { get; set; }

    public string? ImpersonatorTenantName { get; set; }

    public DateTime ExecutionTime { get; set; }

    public int ExecutionDuration { get; set; }

    public string? ClientId { get; set; }

    public string? CorrelationId { get; set; }

    public string? ClientIpAddress { get; set; }

    public string? ClientName { get; set; }

    public string? BrowserInfo { get; set; }

    public string? HttpMethod { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? Url { get; set; }

    public string? TraceId { get; set; }

    public bool IsTruncated { get; set; }

    public int PayloadBytes { get; set; }

    public int RedactionVersion { get; set; }

    public bool RedactionApplied { get; set; }

    public List<AuditLogActionInfo> Actions { get; set; } = [];

    public List<AuditLogExceptionInfo> Exceptions { get; } = [];

    public Dictionary<string, string?> ExtraProperties { get; } = new(StringComparer.Ordinal);

    public List<EntityChangeInfo> EntityChanges { get; } = [];

    public List<string> Comments { get; set; } = [];
}

[Serializable]
public sealed class AuditLogActionInfo
{
    public string ServiceName { get; set; } = string.Empty;

    public string MethodName { get; set; } = string.Empty;

    public string Parameters { get; set; } = "{}";

    public DateTime ExecutionTime { get; set; }

    public int ExecutionDuration { get; set; }

    public string? Result { get; set; }

    public Dictionary<string, string?> ExtraProperties { get; } = new(StringComparer.Ordinal);
}

[Serializable]
public sealed class AuditLogExceptionInfo
{
    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? StackTrace { get; set; }
}

[Serializable]
public sealed class EntityChangeInfo
{
    public string EntityTypeFullName { get; set; } = string.Empty;

    public string ChangeType { get; set; } = string.Empty;

    public string? EntityId { get; set; }

    public Dictionary<string, string?>? EntityKeys { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? UserId { get; set; }

    public DateTime ChangeTime { get; set; }

    public List<EntityPropertyChangeInfo> PropertyChanges { get; } = [];

    public object? Entity { get; set; }

    public IReadOnlyList<string>? KeyPropertyNames { get; set; }

    public void Merge(EntityChangeInfo other)
    {
        if (other == null)
        {
            return;
        }

        foreach (var property in other.PropertyChanges)
        {
            if (PropertyChanges.Any(p => string.Equals(p.PropertyName, property.PropertyName, StringComparison.Ordinal)))
            {
                continue;
            }

            PropertyChanges.Add(property);
        }
    }
}

[Serializable]
public sealed class EntityPropertyChangeInfo
{
    public string PropertyName { get; set; } = string.Empty;

    public string? PropertyTypeFullName { get; set; }

    public string? OriginalValue { get; set; }

    public string? NewValue { get; set; }
}

public sealed class AuditLogContributionContext
{
    public IServiceProvider ServiceProvider { get; }

    public AuditLogInfo AuditInfo { get; }

    public AuditLogContributionContext(IServiceProvider serviceProvider, AuditLogInfo auditInfo)
    {
        ServiceProvider = serviceProvider;
        AuditInfo = auditInfo;
    }
}

public abstract class AuditLogContributor
{
    public virtual void PreContribute(AuditLogContributionContext context)
    {
    }

    public virtual void PostContribute(AuditLogContributionContext context)
    {
    }
}
