using System.Collections.Generic;

namespace Shared.AspNetCore.ProblemDetails;

/// <summary>
/// OpenAPI contract for every TenantWright RFC 7807 error response.
/// Runtime responses are written with ASP.NET Core <c>ProblemDetails</c>, while
/// this type makes the stable extensions visible to generated consumers.
/// </summary>
public sealed class SharedHttpProblemDetails
{
    public string Type { get; init; } = default!;

    public string Title { get; init; } = default!;

    public int Status { get; init; }

    public string Detail { get; init; } = default!;

    public string Code { get; init; } = default!;

    public string TraceId { get; init; } = default!;

    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}
