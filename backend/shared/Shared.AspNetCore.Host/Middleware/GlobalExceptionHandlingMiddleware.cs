using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.AspNetCore.ProblemDetails;
using Shared.MultiTenancy;

namespace Shared.AspNetCore.Host.Middleware;

public class GlobalExceptionHandlingMiddleware
{
    private const int ClientClosedRequestStatusCode = 499;
    private const string UnexpectedErrorMessage = "An unexpected error occurred.";

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted || IsOpenIddictProtocolPath(context.Request.Path))
            {
                throw;
            }

            var classification = Classify(exception, context.RequestAborted.IsCancellationRequested);
            using var scope = BeginRequestErrorScope(context, classification);
            LogException(exception, classification);

            if (classification.SuppressResponseBody)
            {
                context.Response.StatusCode = classification.StatusCode;
                context.Response.Headers.CacheControl = "no-store";
                return;
            }

            await WriteProblemDetailsAsync(context, exception, classification);
        }
    }

    private IDisposable? BeginRequestErrorScope(HttpContext context, ErrorClassification classification)
    {
        var services = context.RequestServices;
        var tenantId = services?.GetService<ICurrentTenant>()?.Id;
        var userSubject = context.User.FindFirst("sub")?.Value
                          ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return _logger.BeginScope(new Dictionary<string, object?>
        {
            ["RequestMethod"] = context.Request.Method,
            ["RequestPath"] = context.Request.Path.Value,
            ["ResponseStatusCode"] = classification.StatusCode,
            ["ErrorCode"] = classification.Code,
            ["TenantId"] = tenantId,
            ["UserSubject"] = userSubject
        });
    }

    private void LogException(Exception exception, ErrorClassification classification)
    {
        if (classification.StatusCode == ClientClosedRequestStatusCode)
        {
            _logger.LogDebug(
                "Request was cancelled with HTTP status {StatusCode} and code {ErrorCode}.",
                classification.StatusCode,
                classification.Code);
            return;
        }

        if (classification.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(
                exception,
                "Request failed with HTTP status {StatusCode} and code {ErrorCode}.",
                classification.StatusCode,
                classification.Code);
            return;
        }

        if (classification.StatusCode == StatusCodes.Status403Forbidden)
        {
            _logger.LogWarning(
                "Request was forbidden with HTTP status {StatusCode} and code {ErrorCode}.",
                classification.StatusCode,
                classification.Code);
            return;
        }

        _logger.LogInformation(
            "Request failed with HTTP status {StatusCode} and code {ErrorCode}.",
            classification.StatusCode,
            classification.Code);
    }

    private async Task WriteProblemDetailsAsync(
        HttpContext context,
        Exception exception,
        ErrorClassification classification)
    {
        var publicDetail = exception is SharedException sharedException
            ? sharedException.PublicDetail
            : classification.PublicDetail;
        var detail = publicDetail
            ?? (_environment.IsDevelopment()
                ? exception.Message
                : UnexpectedErrorMessage);
        var developmentDetails = _environment.IsDevelopment()
            ? exception.ToString()
            : null;

        var problemDetails = SharedProblemDetailsFactory.CreateProblemDetails(
            context,
            classification.StatusCode,
            classification.Code,
            classification.Title,
            detail,
            developmentDetails);

        if (exception is SharedValidationException { Errors: { Count: > 0 } } validationException)
        {
            var validationProblemDetails = new Microsoft.AspNetCore.Mvc.ValidationProblemDetails(
                new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary())
            {
                Type = problemDetails.Type,
                Title = problemDetails.Title,
                Status = problemDetails.Status,
                Detail = problemDetails.Detail
            };

            foreach (var error in validationException.Errors)
            {
                // Normalized for the same reason ModelState keys are: a caller matching these against its own
                // field names must not have to know which path produced them.
                validationProblemDetails.Errors[ValidationErrorKey.Normalize(error.Key)] = error.Value;
            }

            foreach (var extension in problemDetails.Extensions)
            {
                validationProblemDetails.Extensions[extension.Key] = extension.Value;
            }

            await SharedProblemDetailsWriter.WriteAsync(context, validationProblemDetails);
            return;
        }

        await SharedProblemDetailsWriter.WriteAsync(context, problemDetails);
    }

    private static bool IsOpenIddictProtocolPath(PathString path)
        => path.StartsWithSegments("/connect", StringComparison.Ordinal) || path.StartsWithSegments("/.well-known", StringComparison.Ordinal);

    private static ErrorClassification Classify(Exception exception, bool requestAborted)
    {
        if (exception is OperationCanceledException)
        {
            return requestAborted
                ? new ErrorClassification(
                    ClientClosedRequestStatusCode,
                    SharedErrorCodes.RequestCancelled,
                    "Request Cancelled",
                    SuppressResponseBody: true)
                : new ErrorClassification(
                    StatusCodes.Status504GatewayTimeout,
                    SharedErrorCodes.RequestTimeout,
                    "Request Timeout",
                    PublicDetail: "The request timed out.");
        }

        return exception switch
        {
            SharedTimeoutException timeoutException => new ErrorClassification(
                StatusCodes.Status504GatewayTimeout,
                timeoutException.Code,
                "Request Timeout"),
            SharedUnauthorizedException unauthorizedException => new ErrorClassification(
                StatusCodes.Status401Unauthorized,
                unauthorizedException.Code,
                "Unauthorized"),
            SharedForbiddenException forbiddenException => new ErrorClassification(
                StatusCodes.Status403Forbidden,
                forbiddenException.Code,
                "Forbidden"),
            UnauthorizedAccessException => new ErrorClassification(
                StatusCodes.Status403Forbidden,
                SharedErrorCodes.Forbidden,
                "Forbidden",
                PublicDetail: "You are not allowed to perform this action."),
            SharedConflictException conflictException => new ErrorClassification(
                StatusCodes.Status409Conflict,
                conflictException.Code,
                "Conflict"),
            SharedValidationException validationException => new ErrorClassification(
                StatusCodes.Status400BadRequest,
                validationException.Code,
                "Bad Request"),
            ArgumentException when exception is not ArgumentNullException and not ArgumentOutOfRangeException =>
                new ErrorClassification(
                    StatusCodes.Status400BadRequest,
                    SharedErrorCodes.ValidationFailed,
                    "Bad Request",
                    PublicDetail: "The request is invalid."),
            _ when IsEntityNotFoundException(exception) => new ErrorClassification(
                StatusCodes.Status404NotFound,
                exception is SharedException sharedException
                    ? sharedException.Code
                    : SharedErrorCodes.ResourceNotFound,
                "Not Found"),
            _ when IsConcurrencyException(exception) => new ErrorClassification(
                StatusCodes.Status409Conflict,
                SharedErrorCodes.ConcurrencyConflict,
                "Conflict",
                PublicDetail: "The record changed while the request was being processed. Reload and try again."),
            _ => new ErrorClassification(
                StatusCodes.Status500InternalServerError,
                SharedErrorCodes.InternalError,
                "Internal Server Error")
        };
    }

    private static bool IsEntityNotFoundException(Exception exception)
    {
        return IsExceptionType(exception, "Shared.Domain.Entities.EntityNotFoundException");
    }

    private static bool IsConcurrencyException(Exception exception)
    {
        return IsExceptionType(exception, "Shared.Data.Exceptions.SharedDbConcurrencyException")
               || IsExceptionType(exception, "Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException");
    }

    private static bool IsExceptionType(Exception exception, string expectedTypeFullName)
    {
        for (var current = exception.GetType(); current != null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, expectedTypeFullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record ErrorClassification(
        int StatusCode,
        string Code,
        string Title,
        bool SuppressResponseBody = false,
        string? PublicDetail = null);
}
