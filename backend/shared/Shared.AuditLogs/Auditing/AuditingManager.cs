using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;

namespace Shared.AuditLogs.Auditing;

public sealed class AuditingManager : IAuditingManager, IScopedDependency
{
    private static readonly AsyncLocal<Stack<IAuditLogScope>> ScopeStack = new();

    private readonly IAuditingHelper _auditingHelper;
    private readonly IAuditingStore _auditingStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<SharedAuditingOptions> _options;
    private readonly ILogger<AuditingManager> _logger;

    public AuditingManager(
        IAuditingHelper auditingHelper,
        IAuditingStore auditingStore,
        IServiceProvider serviceProvider,
        IOptions<SharedAuditingOptions> options,
        ILogger<AuditingManager> logger)
    {
        _auditingHelper = auditingHelper;
        _auditingStore = auditingStore;
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    public IAuditLogScope? Current
    {
        get
        {
            var stack = ScopeStack.Value;
            if (stack == null || stack.Count == 0)
            {
                return null;
            }

            return stack.Peek();
        }
    }

    public IAuditLogSaveHandle BeginScope()
    {
        var stack = ScopeStack.Value ??= new Stack<IAuditLogScope>();
        var scope = new AuditLogScope(_auditingHelper.CreateAuditLogInfo());
        stack.Push(scope);

        return new SaveHandle(this, scope, Stopwatch.GetTimestamp());
    }

    private static void PopScope(IAuditLogScope scope)
    {
        var stack = ScopeStack.Value;
        if (stack == null || stack.Count == 0)
        {
            return;
        }

        if (ReferenceEquals(stack.Peek(), scope))
        {
            stack.Pop();
            return;
        }

        var remaining = stack.Where(x => !ReferenceEquals(x, scope)).Reverse().ToList();
        stack.Clear();
        foreach (var item in remaining)
        {
            stack.Push(item);
        }
    }

    private void BeforeSave(AuditLogInfo auditLog, long startedTimestamp)
    {
        auditLog.ExecutionDuration = (int)Math.Max(0, Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
        ExecutePostContributors(auditLog);
        MergeEntityChanges(auditLog);
    }

    private void ExecutePostContributors(AuditLogInfo auditLogInfo)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = new AuditLogContributionContext(scope.ServiceProvider, auditLogInfo);

        foreach (var contributor in _options.Value.Contributors)
        {
            try
            {
                contributor.PostContribute(context);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit post-contributor failed.");
            }
        }
    }

    private static void MergeEntityChanges(AuditLogInfo auditLog)
    {
        var changeGroups = auditLog.EntityChanges
            .Where(x => string.Equals(x.ChangeType, "Modified", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.ChangeType, "Updated", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => new { x.EntityTypeFullName, x.EntityId })
            .ToList();

        foreach (var changeGroup in changeGroups)
        {
            if (changeGroup.Count() <= 1)
            {
                continue;
            }

            var firstEntityChange = changeGroup.First();
            foreach (var entityChangeInfo in changeGroup.Skip(1).ToList())
            {
                firstEntityChange.Merge(entityChangeInfo);
                auditLog.EntityChanges.Remove(entityChangeInfo);
            }
        }
    }

    private sealed class SaveHandle : IAuditLogSaveHandle
    {
        private readonly AuditingManager _manager;
        private readonly IAuditLogScope _scope;
        private readonly long _startedTimestamp;
        private bool _disposed;

        public SaveHandle(AuditingManager manager, IAuditLogScope scope, long startedTimestamp)
        {
            _manager = manager;
            _scope = scope;
            _startedTimestamp = startedTimestamp;
        }

        public async Task SaveAsync()
        {
            _manager.BeforeSave(_scope.Log, _startedTimestamp);
            await _manager._auditingStore.SaveAsync(_scope.Log);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AuditingManager.PopScope(_scope);
        }
    }
}
