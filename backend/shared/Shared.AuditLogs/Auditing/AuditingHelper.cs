using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.CurrentUser;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Shared.AuditLogs.Auditing;

public sealed class AuditingHelper : IAuditingHelper, ITransientDependency
{
    private static readonly AsyncLocal<int> DisabledScopeCounter = new();

    private readonly IOptions<SharedAuditingOptions> _options;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTenant _currentTenant;
    private readonly IAuditSerializer _auditSerializer;
    private readonly ILogger<AuditingHelper> _logger;
    private readonly IServiceProvider _serviceProvider;

    public AuditingHelper(
        IOptions<SharedAuditingOptions> options,
        ICurrentUser currentUser,
        ICurrentTenant currentTenant,
        IAuditSerializer auditSerializer,
        ILogger<AuditingHelper> logger,
        IServiceProvider serviceProvider)
    {
        _options = options;
        _currentUser = currentUser;
        _currentTenant = currentTenant;
        _auditSerializer = auditSerializer;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public bool ShouldSaveAudit(
        MethodInfo? methodInfo,
        bool defaultValue = false,
        bool ignoreIntegrationServiceAttribute = false)
    {
        if (methodInfo == null || !methodInfo.IsPublic)
        {
            return false;
        }

        if (!IsAuditingEnabled())
        {
            return false;
        }

        if (methodInfo.IsDefined(typeof(AuditedAttribute), true))
        {
            return true;
        }

        if (methodInfo.IsDefined(typeof(DisableAuditingAttribute), true))
        {
            return false;
        }

        var classType = methodInfo.DeclaringType;
        if (classType != null)
        {
            var shouldAudit = AuditingInterceptorRegistrar.ShouldAuditTypeByDefaultOrNull(
                classType,
                ignoreIntegrationServiceAttribute);
            if (shouldAudit.HasValue)
            {
                return shouldAudit.Value;
            }
        }

        return false;
    }

    public bool IsEntityHistoryEnabled(Type entityType, bool defaultValue = false)
    {
        if (!entityType.IsPublic)
        {
            return false;
        }

        var options = _options.Value;
        if (options.IgnoredTypes.Any(t => t.IsAssignableFrom(entityType)))
        {
            return false;
        }

        if (entityType.IsDefined(typeof(AuditedAttribute), true))
        {
            return true;
        }

        foreach (var propertyInfo in entityType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (propertyInfo.IsDefined(typeof(AuditedAttribute), true))
            {
                return true;
            }
        }

        if (entityType.IsDefined(typeof(DisableAuditingAttribute), true))
        {
            return false;
        }

        return false;
    }

    public AuditLogInfo CreateAuditLogInfo()
    {
        var info = new AuditLogInfo
        {
            ApplicationName = _options.Value.ApplicationName,
            TenantId = _currentTenant.Id,
            UserId = _currentUser.Id?.ToString("D"),
            UserName = _currentUser.UserName,
            ExecutionTime = DateTime.UtcNow,
            RedactionApplied = false,
            RedactionVersion = 0
        };

        ExecutePreContributors(info);
        return info;
    }

    public AuditLogActionInfo CreateAuditLogAction(Type? type, MethodInfo method, object?[] arguments)
    {
        return CreateAuditLogAction(
            type,
            method,
            CreateArgumentsDictionary(method, arguments));
    }

    public AuditLogActionInfo CreateAuditLogAction(Type? type, MethodInfo method, IDictionary<string, object?> arguments)
    {
        return new AuditLogActionInfo
        {
            ServiceName = type?.FullName ?? string.Empty,
            MethodName = method.Name,
            Parameters = SerializeArguments(arguments),
            ExecutionTime = DateTime.UtcNow
        };
    }

    public IDisposable DisableAuditing()
    {
        DisabledScopeCounter.Value++;
        return new DisableAuditingScope();
    }

    public bool IsAuditingEnabled()
    {
        return _options.Value.IsEnabled && DisabledScopeCounter.Value <= 0;
    }

    private void ExecutePreContributors(AuditLogInfo auditLogInfo)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = new AuditLogContributionContext(scope.ServiceProvider, auditLogInfo);

        foreach (var contributor in _options.Value.Contributors)
        {
            try
            {
                contributor.PreContribute(context);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit pre-contributor failed.");
            }
        }
    }

    private string SerializeArguments(IDictionary<string, object?> arguments)
    {
        try
        {
            if (arguments.Count == 0)
            {
                return "{}";
            }

            var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var argument in arguments)
            {
                var value = argument.Value;
                if (value != null &&
                    (_options.Value.IgnoredTypes.Any(t => t.IsInstanceOfType(value)) ||
                     value.GetType().IsDefined(typeof(DisableAuditingAttribute), inherit: true)))
                {
                    dictionary[argument.Key] = null;
                    continue;
                }

                dictionary[argument.Key] = value;
            }

            return _auditSerializer.Serialize(dictionary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to serialize audit action parameters.");
            return "{}";
        }
    }

    private static Dictionary<string, object?> CreateArgumentsDictionary(MethodInfo method, object?[] arguments)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        var dictionary = new Dictionary<string, object?>(parameters.Length, StringComparer.Ordinal);
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameterName = parameters[index].Name ?? $"arg{index}";
            var parameterValue = index < arguments.Length ? arguments[index] : null;
            dictionary[parameterName] = parameterValue;
        }

        return dictionary;
    }

    private sealed class DisableAuditingScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisabledScopeCounter.Value = Math.Max(0, DisabledScopeCounter.Value - 1);
        }
    }
}
