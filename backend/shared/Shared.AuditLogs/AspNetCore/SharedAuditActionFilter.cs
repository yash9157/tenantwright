using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.Auditing;
using Shared.Modularity.DependencyInjection;

namespace Shared.AuditLogs.AspNetCore;

public sealed class SharedAuditActionFilter : IAsyncActionFilter, ITransientDependency
{
    private readonly IOptions<SharedAuditingOptions> _options;
    private readonly IAuditingManager _auditingManager;
    private readonly IAuditingHelper _auditingHelper;
    private readonly IAuditPolicyEvaluator _auditPolicyEvaluator;

    public SharedAuditActionFilter(
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

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!ShouldSaveAudit(context, out var auditLog, out var auditLogAction))
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
        ActionExecutingContext context,
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

        if (context.ActionDescriptor is not ControllerActionDescriptor actionDescriptor)
        {
            return false;
        }

        var auditScope = _auditingManager.Current;
        if (auditScope == null)
        {
            return false;
        }

        var controllerType = actionDescriptor.ControllerTypeInfo.AsType();
        var shouldAuditByRuntimeRules = _auditingHelper.ShouldSaveAudit(
                actionDescriptor.MethodInfo,
                defaultValue: false,
                ignoreIntegrationServiceAttribute: options.IsEnabledForIntegrationServices);
        var policyResult = _auditPolicyEvaluator.EvaluateControllerPolicy(
            controllerType,
            actionDescriptor.MethodInfo,
            shouldAuditByRuntimeRules,
            options);
        context.HttpContext.Items[AuditingHttpContextItems.PolicyResultKey] = policyResult;
        if (!policyResult.ShouldAudit)
        {
            return false;
        }

        auditLog = auditScope.Log;
        ApplyCategoryOverride(auditLog, actionDescriptor.MethodInfo, controllerType);

        if (!options.DisableLogActionInfo)
        {
            auditLogAction = _auditingHelper.CreateAuditLogAction(
                actionDescriptor.ControllerTypeInfo.AsType(),
                actionDescriptor.MethodInfo,
                new Dictionary<string, object?>(context.ActionArguments, StringComparer.Ordinal));
        }

        return true;
    }

    private static void ApplyCategoryOverride(AuditLogInfo auditLog, MethodInfo methodInfo, Type controllerType)
    {
        var category =
            methodInfo.GetCustomAttribute<AuditedAttribute>(inherit: true)?.Category ??
            controllerType.GetCustomAttribute<AuditedAttribute>(inherit: true)?.Category;
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
