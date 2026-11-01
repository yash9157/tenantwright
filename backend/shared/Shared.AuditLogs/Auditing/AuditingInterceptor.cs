using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Castle.DynamicProxy;
using Microsoft.Extensions.Options;
using Shared.CurrentUser;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Abstractions;

namespace Shared.AuditLogs.Auditing;

[ExposeServices(typeof(AuditingInterceptor))]
public sealed class AuditingInterceptor : IInterceptor, ITransientDependency
{
    private static readonly MethodInfo InternalInterceptAsyncGenericMethodDefinition =
        typeof(AuditingInterceptor).GetMethod(nameof(InternalInterceptAsyncGeneric), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly ConcurrentDictionary<Type, MethodInfo> InternalInterceptAsyncGenericMethods = new();
    private static readonly Meter Meter = new("Shared.AuditLogs.InterceptorPolicy");
    private static readonly Counter<long> PolicyDecisionCounter = Meter.CreateCounter<long>("audit.interceptor.policy.decision.count");

    private readonly IAuditingManager _auditingManager;
    private readonly IAuditingHelper _auditingHelper;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IOptions<SharedAuditingOptions> _options;
    private readonly IAuditPolicyEvaluator _auditPolicyEvaluator;

    public AuditingInterceptor(
        IAuditingManager auditingManager,
        IAuditingHelper auditingHelper,
        ICurrentUser currentUser,
        IUnitOfWorkManager unitOfWorkManager,
        IOptions<SharedAuditingOptions> options,
        IAuditPolicyEvaluator auditPolicyEvaluator)
    {
        _auditingManager = auditingManager;
        _auditingHelper = auditingHelper;
        _currentUser = currentUser;
        _unitOfWorkManager = unitOfWorkManager;
        _options = options;
        _auditPolicyEvaluator = auditPolicyEvaluator;
    }

    public void Intercept(IInvocation invocation)
    {
        var method = invocation.MethodInvocationTarget ?? invocation.Method;
        var options = _options.Value;
        if (!options.IsEnabled)
        {
            invocation.Proceed();
            return;
        }

        var shouldAuditByRuntimeRules = _auditingHelper.ShouldSaveAudit(
            method,
            defaultValue: false,
            ignoreIntegrationServiceAttribute: options.IsEnabledForIntegrationServices);
        var policyResult = _auditPolicyEvaluator.EvaluateServicePolicy(
            invocation.TargetType,
            method,
            shouldAuditByRuntimeRules,
            options);
        if (!policyResult.ShouldAudit)
        {
            PolicyDecisionCounter.Add(
                1,
                new KeyValuePair<string, object?>("decision", policyResult.Decision.ToString()),
                new KeyValuePair<string, object?>("reason", policyResult.Reason));
            invocation.Proceed();
            return;
        }

        if (policyResult.IsObserveOnly)
        {
            PolicyDecisionCounter.Add(
                1,
                new KeyValuePair<string, object?>("decision", "ObserveOnly"),
                new KeyValuePair<string, object?>("reason", policyResult.Reason));
        }

        if (!typeof(Task).IsAssignableFrom(invocation.Method.ReturnType))
        {
            InterceptSync(invocation, method);
            return;
        }

        InterceptAsync(invocation, method);
    }

    private void InterceptSync(IInvocation invocation, MethodInfo method)
    {
        var saveHandle = _auditingManager.Current == null
            ? _auditingManager.BeginScope()
            : null;

        var scope = _auditingManager.Current;
        var auditLog = scope?.Log;
        ApplyCategoryOverride(auditLog, method, invocation.TargetType, saveHandle != null);
        var action = CreateAuditAction(invocation, method, auditLog);
        var started = Stopwatch.GetTimestamp();
        var hasError = false;

        try
        {
            invocation.Proceed();
            if (action != null)
            {
                action.Result = "success";
            }
        }
        catch (Exception ex)
        {
            hasError = true;
            AddException(auditLog, ex);
            if (action != null)
            {
                action.Result = "failure";
            }

            throw;
        }
        finally
        {
            FinalizeAction(auditLog, action, started);
            if (saveHandle != null)
            {
                CompleteScopeAsync(saveHandle, method, auditLog, hasError).GetAwaiter().GetResult();
            }
        }
    }

    private void InterceptAsync(IInvocation invocation, MethodInfo method)
    {
        var returnType = invocation.Method.ReturnType;
        if (returnType == typeof(Task))
        {
            invocation.ReturnValue = InternalInterceptAsync(invocation, method);
            return;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var closed = InternalInterceptAsyncGenericMethods.GetOrAdd(
                resultType,
                static type => InternalInterceptAsyncGenericMethodDefinition.MakeGenericMethod(type));
            invocation.ReturnValue = closed.Invoke(this, [invocation, method]);
            return;
        }

        invocation.Proceed();
    }

    private async Task InternalInterceptAsync(IInvocation invocation, MethodInfo method)
    {
        var saveHandle = _auditingManager.Current == null
            ? _auditingManager.BeginScope()
            : null;

        var scope = _auditingManager.Current;
        var auditLog = scope?.Log;
        ApplyCategoryOverride(auditLog, method, invocation.TargetType, saveHandle != null);
        var action = CreateAuditAction(invocation, method, auditLog);
        var started = Stopwatch.GetTimestamp();
        var hasError = false;

        try
        {
            invocation.Proceed();
            if (invocation.ReturnValue is not Task task)
            {
                throw new InvalidOperationException($"Expected return value of type Task for method {invocation.Method.Name}.");
            }

            await task;
            if (action != null)
            {
                action.Result = "success";
            }
        }
        catch (Exception ex)
        {
            hasError = true;
            AddException(auditLog, ex);
            if (action != null)
            {
                action.Result = "failure";
            }

            throw;
        }
        finally
        {
            FinalizeAction(auditLog, action, started);
            if (saveHandle != null)
            {
                await CompleteScopeAsync(saveHandle, method, auditLog, hasError);
            }
        }
    }

    private async Task<T> InternalInterceptAsyncGeneric<T>(IInvocation invocation, MethodInfo method)
    {
        var saveHandle = _auditingManager.Current == null
            ? _auditingManager.BeginScope()
            : null;

        var scope = _auditingManager.Current;
        var auditLog = scope?.Log;
        ApplyCategoryOverride(auditLog, method, invocation.TargetType, saveHandle != null);
        var action = CreateAuditAction(invocation, method, auditLog);
        var started = Stopwatch.GetTimestamp();
        var hasError = false;

        try
        {
            invocation.Proceed();
            if (invocation.ReturnValue is not Task<T> task)
            {
                throw new InvalidOperationException($"Expected return value of type Task<{typeof(T).Name}> for method {invocation.Method.Name}.");
            }

            var result = await task;
            if (action != null)
            {
                action.Result = "success";
            }

            return result;
        }
        catch (Exception ex)
        {
            hasError = true;
            AddException(auditLog, ex);
            if (action != null)
            {
                action.Result = "failure";
            }

            throw;
        }
        finally
        {
            FinalizeAction(auditLog, action, started);
            if (saveHandle != null)
            {
                await CompleteScopeAsync(saveHandle, method, auditLog, hasError);
            }
        }
    }

    private static void ApplyCategoryOverride(AuditLogInfo? auditLog, MethodInfo method, Type? targetType, bool isOwningScope)
    {
        // Only override when this interceptor owns the scope (i.e. no HTTP action/page filter already
        // set a category for this request) - otherwise a nested service call could clobber the
        // category an outer HTTP-level [Audited(Category=...)] already applied.
        if (auditLog == null || !isOwningScope)
        {
            return;
        }

        var category =
            method.GetCustomAttribute<AuditedAttribute>(inherit: true)?.Category ??
            targetType?.GetCustomAttribute<AuditedAttribute>(inherit: true)?.Category;
        if (!string.IsNullOrWhiteSpace(category))
        {
            auditLog.Category = category.Trim();
        }
    }

    private AuditLogActionInfo? CreateAuditAction(IInvocation invocation, MethodInfo method, AuditLogInfo? auditLog)
    {
        if (auditLog == null || _options.Value.DisableLogActionInfo)
        {
            return null;
        }

        return _auditingHelper.CreateAuditLogAction(
            invocation.TargetType,
            method,
            invocation.Arguments);
    }

    private static void FinalizeAction(AuditLogInfo? auditLog, AuditLogActionInfo? action, long started)
    {
        if (auditLog == null || action == null)
        {
            return;
        }

        action.ExecutionDuration = (int)Math.Max(0, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        auditLog.Actions.Add(action);
    }

    private static void AddException(AuditLogInfo? auditLog, Exception exception)
    {
        if (auditLog == null)
        {
            return;
        }

        auditLog.Exceptions.Add(new AuditLogExceptionInfo
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            StackTrace = exception.StackTrace
        });
    }

    private async Task CompleteScopeAsync(
        IAuditLogSaveHandle saveHandle,
        MethodInfo method,
        AuditLogInfo? auditLog,
        bool hasError)
    {
        try
        {
            if (auditLog == null)
            {
                return;
            }

            var hasScopeError = hasError;
            if (TryGetCurrentUnitOfWork() is { } currentUnitOfWork)
            {
                try
                {
                    await currentUnitOfWork.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    hasScopeError = true;
                    AddException(auditLog, ex);
                }
            }

            if (await ShouldWriteAuditLogAsync(method, auditLog, hasScopeError))
            {
                await saveHandle.SaveAsync();
            }
        }
        finally
        {
            saveHandle.Dispose();
        }
    }

    private IUnitOfWork? TryGetCurrentUnitOfWork()
    {
        var current = _unitOfWorkManager.Current;
        if (current == null || current.IsDisposed || current.IsCompleted || current.IsReserved)
        {
            return null;
        }

        return current;
    }

    private async Task<bool> ShouldWriteAuditLogAsync(MethodInfo method, AuditLogInfo auditLogInfo, bool hasError)
    {
        foreach (var selector in _options.Value.AlwaysLogSelectors)
        {
            if (await selector(auditLogInfo))
            {
                return true;
            }
        }

        if (_options.Value.AlwaysLogOnException && hasError)
        {
            return true;
        }

        if (!_options.Value.IsEnabledForAnonymousUsers && !_currentUser.IsAuthenticated)
        {
            return false;
        }

        if (!_options.Value.IsEnabledForGetRequests &&
            method.Name.StartsWith("Get", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
