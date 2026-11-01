using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;

namespace Shared.AuditLogs.Middleware;

public sealed class AuditLogCaptureMiddleware : IMiddleware, ITransientDependency
{
    private static readonly Meter Meter = new("Shared.AspNetCore.Host.Auditing");
    private static readonly Counter<long> PublishCounter = Meter.CreateCounter<long>("audit.producer.publish.count");
    private static readonly Counter<long> PublishFailureCounter = Meter.CreateCounter<long>("audit.producer.publish.failure.count");
    private static readonly Counter<long> DropCounter = Meter.CreateCounter<long>("audit.producer.drop.count");
    private static readonly Counter<long> RedactionHitCounter = Meter.CreateCounter<long>("audit.producer.redaction.hit.count");
    private static readonly Histogram<double> ProducerOverheadMs =
        Meter.CreateHistogram<double>("audit.producer.overhead.duration.ms");

    private readonly IOptionsMonitor<SharedAuditingOptions> _auditingOptionsMonitor;
    private readonly IOptions<SharedAspNetCoreAuditingOptions> _aspNetCoreOptions;
    private readonly IOptions<AuditRedactionOptions> _redactionOptions;
    private readonly IAuditingManager _auditingManager;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly ILogger<AuditLogCaptureMiddleware> _logger;

    public AuditLogCaptureMiddleware(
        IOptionsMonitor<SharedAuditingOptions> auditingOptionsMonitor,
        IOptions<SharedAspNetCoreAuditingOptions> aspNetCoreOptions,
        IOptions<AuditRedactionOptions> redactionOptions,
        IAuditingManager auditingManager,
        IUnitOfWorkManager unitOfWorkManager,
        ILogger<AuditLogCaptureMiddleware> logger)
    {
        _auditingOptionsMonitor = auditingOptionsMonitor;
        _aspNetCoreOptions = aspNetCoreOptions;
        _redactionOptions = redactionOptions;
        _auditingManager = auditingManager;
        _unitOfWorkManager = unitOfWorkManager;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var options = _auditingOptionsMonitor.CurrentValue;
        if (!options.IsEnabled)
        {
            DropCounter.Add(1, new KeyValuePair<string, object?>("reason", "auditing_disabled"));
            await next(context);
            return;
        }

        if (IsIgnoredUrl(context, options, _aspNetCoreOptions.Value, out var ignoredReason))
        {
            DropCounter.Add(1, new KeyValuePair<string, object?>("reason", ignoredReason));
            await next(context);
            return;
        }

        var publishStarted = Stopwatch.GetTimestamp();
        var hasError = false;

        using var saveHandle = _auditingManager.BeginScope();

        try
        {
            await next(context);

            var currentScope = _auditingManager.Current;
            if (currentScope != null && currentScope.Log.Exceptions.Count > 0)
            {
                hasError = true;
            }
        }
        catch (Exception ex)
        {
            hasError = true;
            AddUnhandledException(ex);
            throw;
        }
        finally
        {
            try
            {
                var currentScope = _auditingManager.Current;
                if (currentScope == null)
                {
                    DropCounter.Add(1, new KeyValuePair<string, object?>("reason", "scope_missing"));
                }
                else
                {
                    if (TryGetCurrentUnitOfWork() is { } currentUnitOfWork)
                    {
                        try
                        {
                            await currentUnitOfWork.SaveChangesAsync();
                        }
                        catch (Exception ex)
                        {
                            hasError = true;
                            AddExceptionIfMissing(currentScope.Log, ex);
                        }
                    }

                    PopulateHttpContextData(currentScope.Log, context, options, hasError);

                    var writeDecision = await ShouldWriteAuditLogAsync(context, currentScope.Log, options, hasError);
                    if (writeDecision.ShouldWrite)
                    {
                        await saveHandle.SaveAsync();
                        PublishCounter.Add(
                            1,
                            new KeyValuePair<string, object?>("method", context.Request.Method),
                            new KeyValuePair<string, object?>("status_code", currentScope.Log.HttpStatusCode));
                    }
                    else
                    {
                        DropCounter.Add(1, new KeyValuePair<string, object?>("reason", writeDecision.Reason));
                    }
                }
            }
            catch (Exception ex)
            {
                PublishFailureCounter.Add(1);

                // Strict fail-closed opt-in (sub-flow 3): for a security-critical category, propagate the
                // audit-write failure so the request fails rather than silently losing the audit trail —
                // even though HideErrors keeps every other category in Observe mode.
                var auditCategory = _auditingManager.Current?.Log.Category;
                if (!options.HideErrors || options.IsStrictFailureCategory(auditCategory))
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
                }

                _logger.LogWarning(ex, "Audit log publish failed for request path {RequestPath}.", context.Request.Path);
            }
            finally
            {
                ProducerOverheadMs.Record(Stopwatch.GetElapsedTime(publishStarted).TotalMilliseconds);
            }
        }
    }

    private void PopulateHttpContextData(
        AuditLogInfo log,
        HttpContext context,
        SharedAuditingOptions options,
        bool hasError)
    {
        var redactionOptions = _redactionOptions.Value;
        var principal = context.User;
        var endpoint = context.GetEndpoint();
        var routeEndpoint = endpoint as RouteEndpoint;

        var statusCode = hasError
            ? StatusCodes.Status500InternalServerError
            : context.Response.StatusCode;

        var userId = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? log.UserId;
        var userName = principal.FindFirstValue("name") ?? principal.Identity?.Name ?? log.UserName;
        var clientIp = context.Connection.RemoteIpAddress?.ToString();
        var sessionId = principal.FindFirstValue("session_id");
        var path = context.Request.Path.Value;

        // Preserve an explicitly-set category (from [Audited(Category=...)] on the controller action or an
        // audited app-service, applied during the MVC pipeline) so security-critical endpoints can opt into
        // Strict fail-closed enforcement. Only default to "http.request" when nothing set one.
        if (string.IsNullOrWhiteSpace(log.Category))
        {
            log.Category = "http.request";
        }

        log.TenantId = ResolveTenantId(context, principal) ?? log.TenantId;
        log.UserId = userId;
        log.UserName = userName;
        log.ClientIpAddress = AuditLogRedactor.HashValue(clientIp);
        log.ClientId = principal.FindFirstValue("client_id") ?? log.ClientId;
        log.CorrelationId = ResolveCorrelationId(context);
        log.TraceId = Activity.Current?.TraceId.ToString();
        log.HttpMethod = context.Request.Method;
        log.Url = path;
        log.HttpStatusCode = statusCode;
        log.BrowserInfo = context.Request.Headers.UserAgent.ToString();

        log.ExtraProperties["traceIdentifier"] = context.TraceIdentifier;
        log.ExtraProperties["routeTemplate"] = routeEndpoint?.RoutePattern.RawText;
        log.ExtraProperties["sessionIdHash"] = AuditLogRedactor.HashValue(sessionId);

        var redactionResult = AuditLogProducerRedaction.ApplyWithResult(log, redactionOptions);

        if (redactionResult.RedactionHits > 0)
        {
            RedactionHitCounter.Add(redactionResult.RedactionHits, new KeyValuePair<string, object?>("path", path));
        }

        if (redactionResult.UnredactablePayloadCount > 0)
        {
            _logger.LogWarning(
                "Audit redaction skipped {UnredactablePayloadCount} non-empty action payload(s) because they were neither valid JSON nor form-encoded. Path={RequestPath}",
                redactionResult.UnredactablePayloadCount,
                path);
        }
    }

    private void AddUnhandledException(Exception ex)
    {
        var current = _auditingManager.Current;
        if (current == null)
        {
            return;
        }

        AddExceptionIfMissing(current.Log, ex);
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

    private static async Task<AuditWriteDecision> ShouldWriteAuditLogAsync(
        HttpContext context,
        AuditLogInfo auditLogInfo,
        SharedAuditingOptions options,
        bool hasError)
    {
        foreach (var selector in options.AlwaysLogSelectors)
        {
            if (await selector(auditLogInfo))
            {
                return AuditWriteDecision.Write;
            }
        }

        if (options.AlwaysLogOnException && hasError)
        {
            return AuditWriteDecision.Write;
        }

        if (options.WriteRequestAuditOnlyForAuditedEndpoints)
        {
            var policyResult = TryGetPolicyResult(context);
            if (policyResult == null)
            {
                return AuditWriteDecision.Skip("policy_explicit_required");
            }

            if (!policyResult.ShouldAudit)
            {
                return AuditWriteDecision.Skip(policyResult.Reason ?? "policy_explicit_required");
            }
        }

        if (!options.IsEnabledForAnonymousUsers && !(context.User.Identity?.IsAuthenticated ?? false))
        {
            return AuditWriteDecision.Skip("anonymous_disabled");
        }

        if (!options.IsEnabledForGetRequests &&
            (HttpMethods.IsGet(context.Request.Method) ||
             HttpMethods.IsHead(context.Request.Method)))
        {
            return AuditWriteDecision.Skip("get_disabled");
        }

        return AuditWriteDecision.Write;
    }

    private static bool IsIgnoredUrl(
        HttpContext context,
        SharedAuditingOptions auditingOptions,
        SharedAspNetCoreAuditingOptions options,
        out string reason)
    {
        reason = "ignored_url";
        var path = context.Request.Path.Value;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!auditingOptions.IsEnabledForIntegrationServices &&
            path.StartsWith($"/{SharedAspNetCoreConsts.DefaultIntegrationServiceApiPrefix}/", StringComparison.OrdinalIgnoreCase))
        {
            reason = "integration_disabled";
            return true;
        }

        return options.IgnoredUrls.Any(url => path.StartsWith(url, StringComparison.OrdinalIgnoreCase));
    }

    private static AuditPolicyResult? TryGetPolicyResult(HttpContext context)
    {
        if (!context.Items.TryGetValue(AuditingHttpContextItems.PolicyResultKey, out var value))
        {
            return null;
        }

        return value as AuditPolicyResult;
    }

    private sealed class AuditWriteDecision
    {
        public static readonly AuditWriteDecision Write = new(true, "audited");

        public bool ShouldWrite { get; }

        public string Reason { get; }

        private AuditWriteDecision(bool shouldWrite, string reason)
        {
            ShouldWrite = shouldWrite;
            Reason = reason;
        }

        public static AuditWriteDecision Skip(string reason)
        {
            return new AuditWriteDecision(false, reason);
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

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Correlation-Id", out var correlationIdHeader))
        {
            var value = correlationIdHeader.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return context.TraceIdentifier;
    }

    private static Guid? ResolveTenantId(HttpContext context, ClaimsPrincipal principal)
    {
        var currentTenant = context.RequestServices.GetService<ICurrentTenant>();
        if (currentTenant?.Id.HasValue == true)
        {
            return currentTenant.Id;
        }

        var tenantClaim = principal.FindFirstValue("tenantid");
        return Guid.TryParse(tenantClaim, out var tenantId) ? tenantId : null;
    }
}
