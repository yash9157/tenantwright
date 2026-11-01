using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;
using Shared.MultiTenancy;

namespace Shared.AspNetCore.Host.Idempotency;

public sealed class IdempotencyActionFilter : IAsyncResourceFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDistributedLock _distributedLock;
    private readonly IIdempotencyStore _store;
    private readonly ICurrentTenant _currentTenant;
    private readonly IOptionsMonitor<IdempotencyOptions> _optionsMonitor;
    private readonly ILogger<IdempotencyActionFilter> _logger;

    public IdempotencyActionFilter(
        IDistributedLock distributedLock,
        IIdempotencyStore store,
        ICurrentTenant currentTenant,
        IOptionsMonitor<IdempotencyOptions> optionsMonitor,
        ILogger<IdempotencyActionFilter> logger)
    {
        _distributedLock = distributedLock;
        _store = store;
        _currentTenant = currentTenant;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!RequiresIdempotency(context))
        {
            await next();
            return;
        }

        var options = _optionsMonitor.CurrentValue;
        var headerName = string.IsNullOrWhiteSpace(options.HeaderName)
            ? "Idempotency-Key"
            : options.HeaderName.Trim();

        if (!context.HttpContext.Request.Headers.TryGetValue(headerName, out var idempotencyHeader) ||
            string.IsNullOrWhiteSpace(idempotencyHeader.ToString()))
        {
            context.Result = new BadRequestObjectResult(new { error = "idempotency_key_required" });
            return;
        }

        var cancellationToken = context.HttpContext.RequestAborted;
        var requestFingerprint = await IdempotencyRecordKeyBuilder.ComputeBodyFingerprintAsync(
            context.HttpContext.Request, cancellationToken);
        var recordKey = await IdempotencyRecordKeyBuilder.BuildAsync(
            context.HttpContext,
            _currentTenant,
            idempotencyHeader.ToString(),
            cancellationToken);
        var inProgressTtl = TimeSpan.FromMinutes(options.InProgressTtlMinutes > 0 ? options.InProgressTtlMinutes : 10);
        var completedTtl = TimeSpan.FromHours(options.CompletedTtlHours > 0 ? options.CompletedTtlHours : 24);

        if (!await TryBeginRequestAsync(context, recordKey, requestFingerprint, inProgressTtl, cancellationToken))
        {
            return;
        }

        var executedContext = await next();

        if (executedContext.Exception is not null && !executedContext.ExceptionHandled)
        {
            await TryRemoveRecordAsync(recordKey, cancellationToken);
            return;
        }

        if (!TryBuildCachedResponse(executedContext.Result, out var statusCode, out var contentType, out var responseBody))
        {
            await TryRemoveRecordAsync(recordKey, cancellationToken);
            return;
        }

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            await TryRemoveRecordAsync(recordKey, cancellationToken);
            return;
        }

        if (!await TryCompleteRequestAsync(
                context,
                recordKey,
                requestFingerprint,
                statusCode,
                contentType,
                responseBody,
                completedTtl,
                cancellationToken))
        {
            return;
        }
    }

    private async Task<bool> TryBeginRequestAsync(
        ResourceExecutingContext context,
        string recordKey,
        string requestFingerprint,
        TimeSpan inProgressTtl,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stateLock = await AcquireStateLockAsync(recordKey, cancellationToken);
            var existing = await _store.GetAsync(recordKey, cancellationToken);
            if (existing != null)
            {
                if (!string.Equals(existing.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
                {
                    context.Result = new ConflictObjectResult(new { error = "idempotency_key_reused" });
                    return false;
                }

                if (existing.State == IdempotencyRecordState.InProgress)
                {
                    context.Result = new ConflictObjectResult(new { error = "duplicate_in_progress" });
                    return false;
                }

                context.Result = ReplayCompletedResponse(existing);
                return false;
            }

            await _store.SetInProgressAsync(recordKey, requestFingerprint, inProgressTtl, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idempotency pre-check failed. Returning fail-closed response.");
            context.Result = CreateFailClosedResult();
            return false;
        }
    }

    private async Task<bool> TryCompleteRequestAsync(
        ResourceExecutingContext context,
        string recordKey,
        string requestFingerprint,
        int statusCode,
        string? contentType,
        string? responseBody,
        TimeSpan completedTtl,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stateLock = await AcquireStateLockAsync(recordKey, cancellationToken);
            await _store.SetCompletedAsync(
                recordKey,
                requestFingerprint,
                statusCode,
                contentType,
                responseBody,
                completedTtl,
                cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idempotency completion write failed. Returning fail-closed response.");
            context.Result = CreateFailClosedResult();
            return false;
        }
    }

    private async Task TryRemoveRecordAsync(string recordKey, CancellationToken cancellationToken)
    {
        try
        {
            await using var stateLock = await AcquireStateLockAsync(recordKey, cancellationToken);
            await _store.RemoveAsync(recordKey, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idempotency record cleanup failed for key {RecordKey}.", recordKey);
        }
    }

    private Task<IDistributedLockHandle> AcquireStateLockAsync(string recordKey, CancellationToken cancellationToken)
    {
        return _distributedLock.AcquireAsync(
            $"api:idempotency:lock:{recordKey}",
            new DistributedLockAcquireOptions
            {
                PolicyName = DistributedLockPolicyNames.ApiIdempotency,
                IgnoreTenant = true
            },
            cancellationToken);
    }

    private static bool RequiresIdempotency(ResourceExecutingContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata == null)
        {
            return false;
        }

        return context.ActionDescriptor.EndpointMetadata
            .OfType<RequireIdempotencyAttribute>()
            .Any();
    }

    private static IActionResult ReplayCompletedResponse(IdempotencyCacheEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ResponseBody))
        {
            return new StatusCodeResult(entry.StatusCode > 0 ? entry.StatusCode : StatusCodes.Status200OK);
        }

        return new ContentResult
        {
            StatusCode = entry.StatusCode > 0 ? entry.StatusCode : StatusCodes.Status200OK,
            ContentType = string.IsNullOrWhiteSpace(entry.ContentType) ? "application/json" : entry.ContentType,
            Content = entry.ResponseBody
        };
    }

    private static ObjectResult CreateFailClosedResult()
    {
        return new ObjectResult(new { error = "idempotency_unavailable" })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };
    }

    private static bool TryBuildCachedResponse(
        IActionResult? result,
        out int statusCode,
        out string? contentType,
        out string? responseBody)
    {
        switch (result)
        {
            case ObjectResult objectResult:
                statusCode = objectResult.StatusCode ?? StatusCodes.Status200OK;
                contentType = objectResult.ContentTypes.FirstOrDefault() ?? "application/json";
                responseBody = JsonSerializer.Serialize(objectResult.Value, JsonOptions);
                return true;

            case JsonResult jsonResult:
                statusCode = jsonResult.StatusCode ?? StatusCodes.Status200OK;
                contentType = jsonResult.ContentType ?? "application/json";
                responseBody = JsonSerializer.Serialize(jsonResult.Value, JsonOptions);
                return true;

            case ContentResult contentResult:
                statusCode = contentResult.StatusCode ?? StatusCodes.Status200OK;
                contentType = contentResult.ContentType;
                responseBody = contentResult.Content;
                return true;

            case StatusCodeResult statusCodeResult:
                statusCode = statusCodeResult.StatusCode;
                contentType = null;
                responseBody = null;
                return true;

            case EmptyResult:
                statusCode = StatusCodes.Status200OK;
                contentType = null;
                responseBody = null;
                return true;

            default:
                statusCode = StatusCodes.Status200OK;
                contentType = null;
                responseBody = null;
                return false;
        }
    }
}
