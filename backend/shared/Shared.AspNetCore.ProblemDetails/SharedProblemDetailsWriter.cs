using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using AspNetProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Shared.AspNetCore.ProblemDetails;

/// <summary>
/// Serializes the shared RFC 7807 contract without coupling callers to a host pipeline.
/// </summary>
public static class SharedProblemDetailsWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(
        HttpContext context,
        AspNetProblemDetails problemDetails,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problemDetails);

        context.Response.StatusCode = problemDetails.Status
            ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = SharedProblemDetailsFactory.ContentType;
        context.Response.Headers.CacheControl = "no-store";

        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            problemDetails,
            problemDetails.GetType(),
            SerializerOptions,
            cancellationToken);
    }
}
