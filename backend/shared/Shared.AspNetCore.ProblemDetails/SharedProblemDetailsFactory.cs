using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using AspNetProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Shared.AspNetCore.ProblemDetails;

/// <summary>
/// Creates the single RFC 7807 response shape used by TenantWright HTTP hosts.
/// </summary>
public static class SharedProblemDetailsFactory
{
    public const string ContentType = "application/problem+json";
    private const string ProblemTypePrefix = "urn:tenantwright:problem:";

    public static AspNetProblemDetails CreateProblemDetails(
        HttpContext context,
        int statusCode,
        string code,
        string title,
        string detail,
        string? developmentDetails = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        var problemDetails = new AspNetProblemDetails
        {
            Type = CreateProblemType(code),
            Title = title,
            Status = statusCode,
            Detail = detail
        };

        AddExtensions(problemDetails, context, code, developmentDetails);
        return problemDetails;
    }

    public static ValidationProblemDetails CreateValidationProblemDetails(
        HttpContext context,
        ModelStateDictionary modelState)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(modelState);

        var problemDetails = new ValidationProblemDetails
        {
            Type = CreateProblemType(SharedErrorCodes.ValidationFailed),
            Title = "Bad Request",
            Status = StatusCodes.Status400BadRequest,
            Detail = "One or more validation errors occurred."
        };

        // Built from ModelState by hand rather than through `new ValidationProblemDetails(modelState)` so the
        // keys can be normalized to the serialized property path the client binds. See ValidationErrorKey.
        foreach (var entry in modelState)
        {
            if (entry.Value.Errors.Count == 0)
            {
                continue;
            }

            var messages = new string[entry.Value.Errors.Count];
            for (var index = 0; index < entry.Value.Errors.Count; index++)
            {
                var error = entry.Value.Errors[index];
                messages[index] = string.IsNullOrWhiteSpace(error.ErrorMessage)
                    // A binding failure carries its reason on the exception, not the message. The exception
                    // text itself is not safe to surface, so it becomes a fixed sentence.
                    ? "The supplied value is not valid."
                    : error.ErrorMessage;
            }

            problemDetails.Errors[ValidationErrorKey.Normalize(entry.Key)] = messages;
        }

        AddExtensions(problemDetails, context, SharedErrorCodes.ValidationFailed, null);
        return problemDetails;
    }

    private static void AddExtensions(
        AspNetProblemDetails problemDetails,
        HttpContext context,
        string code,
        string? developmentDetails)
    {
        problemDetails.Extensions["code"] = code;
        problemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        if (!string.IsNullOrWhiteSpace(developmentDetails))
        {
            problemDetails.Extensions["details"] = developmentDetails;
        }
    }

    private static string CreateProblemType(string code) => $"{ProblemTypePrefix}{code}";
}
