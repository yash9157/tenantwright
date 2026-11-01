using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.Auditing;
using Shared.Modularity.DependencyInjection;

namespace Shared.AuditLogs.AspNetCore;

public sealed class SharedAuditPageFilter : IAsyncPageFilter, ITransientDependency
{
    private readonly IOptions<SharedAuditingOptions> _options;
    private readonly IAuditingManager _auditingManager;
    private readonly IAuditingHelper _auditingHelper;
    private readonly IAuditPolicyEvaluator _auditPolicyEvaluator;

    public SharedAuditPageFilter(
        IOptions<SharedAuditingOptions> options,
        IAuditingManager auditingManager,
        IAuditingHelper auditingHelper,
        IAuditPolicyEvaluator auditPolicyEvaluator)
    {
        _options = options;
        _auditingManager = auditingManager;
        _auditingHelper = auditingHelper;
        _auditPolicyEvaluator = auditPolicyEvaluator;
    }

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
    {
        return Task.CompletedTask;
    }

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (context.HandlerMethod == null || !ShouldSaveAudit(context, out var auditLog, out var auditLogAction))
        {
            await next();
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await next();
            if (result.Exception != null)
            {
                AddExceptionIfMissing(auditLog!, result.Exception);
            }
        }
        catch (Exception ex)
        {
            AddExceptionIfMissing(auditLog!, ex);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            if (auditLogAction != null)
            {
                auditLogAction.ExecutionDuration = Convert.ToInt32(stopwatch.Elapsed.TotalMilliseconds);
                auditLog!.Actions.Add(auditLogAction);
            }
        }
    }

    private bool ShouldSaveAudit(
        PageHandlerExecutingContext context,
        out AuditLogInfo? auditLog,
        out AuditLogActionInfo? auditLogAction)
    {
        auditLog = null;
        auditLogAction = null;

        var options = _options.Value;
        if (!options.IsEnabled)
        {
            return false;
        }

        var auditScope = _auditingManager.Current;
        if (auditScope == null)
        {
            return false;
        }

        var handlerMethod = context.HandlerMethod;
        if (handlerMethod == null)
        {
            return false;
        }

        var declaringType = handlerMethod.MethodInfo.DeclaringType;
        var shouldAuditByRuntimeRules = _auditingHelper.ShouldSaveAudit(
                handlerMethod.MethodInfo,
                defaultValue: false,
                ignoreIntegrationServiceAttribute: options.IsEnabledForIntegrationServices);
        var policyResult = _auditPolicyEvaluator.EvaluateControllerPolicy(
            declaringType ?? typeof(object),
            handlerMethod.MethodInfo,
            shouldAuditByRuntimeRules,
            options);
        context.HttpContext.Items[AuditingHttpContextItems.PolicyResultKey] = policyResult;
        if (!policyResult.ShouldAudit)
        {
            return false;
        }

        auditLog = auditScope.Log;
        if (declaringType != null)
        {
            ApplyCategoryOverride(auditLog, handlerMethod.MethodInfo, declaringType);
        }

        if (!options.DisableLogActionInfo)
        {
            auditLogAction = _auditingHelper.CreateAuditLogAction(
                handlerMethod.MethodInfo.DeclaringType,
                handlerMethod.MethodInfo,
                new Dictionary<string, object?>(context.HandlerArguments, StringComparer.Ordinal));
        }

        return true;
    }

    private static void ApplyCategoryOverride(AuditLogInfo auditLog, MethodInfo methodInfo, Type declaringType)
    {
        var category =
            methodInfo.GetCustomAttribute<AuditedAttribute>(inherit: true)?.Category ??
            declaringType.GetCustomAttribute<AuditedAttribute>(inherit: true)?.Category;
        if (!string.IsNullOrWhiteSpace(category))
        {
            auditLog.Category = category.Trim();
        }
    }

    private static void AddExceptionIfMissing(AuditLogInfo auditLog, Exception exception)
    {
        var type = exception.GetType().FullName ?? exception.GetType().Name;
        var message = exception.Message;
        var stack = exception.StackTrace;

        if (auditLog.Exceptions.Any(x =>
                string.Equals(x.Type, type, StringComparison.Ordinal) &&
                string.Equals(x.Message, message, StringComparison.Ordinal) &&
                string.Equals(x.StackTrace, stack, StringComparison.Ordinal)))
        {
            return;
        }

        auditLog.Exceptions.Add(new AuditLogExceptionInfo
        {
            Type = type,
            Message = message,
            StackTrace = stack
        });
    }
}
