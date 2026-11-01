using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Modularity.DependencyInjection;

namespace Shared.AuditLogs.Auditing;

/// <summary>
/// The structured-log audit store of a bus-free host (<c>Messaging:Provider=None</c>). It records only the category,
/// source application and time of each audit record: never its actors, parameters, payload or other PII.
/// </summary>
[Dependency(ServiceLifetime.Singleton, TryRegister = true)]
public sealed class SimpleLogAuditingStore : IAuditingStore, ISingletonDependency
{
    private readonly ILogger<SimpleLogAuditingStore> _logger;

    public SimpleLogAuditingStore(ILogger<SimpleLogAuditingStore> logger)
    {
        _logger = logger;
    }

    public Task SaveAsync(AuditLogInfo auditInfo)
    {
        Record(_logger, auditInfo.Category, auditInfo.ApplicationName, auditInfo.ExecutionTime);
        return Task.CompletedTask;
    }

    /// <summary>The single log shape every bus-free audit path writes, so log-based audit evidence stays uniform.</summary>
    internal static void Record(
        ILogger<SimpleLogAuditingStore> logger,
        string? category,
        string? application,
        DateTime executionTime)
    {
        logger.LogInformation(
            "Fallback audit store used. Category={Category}, Application={Application}, ExecutionTime={ExecutionTime:O}",
            category,
            application,
            executionTime);
    }
}
