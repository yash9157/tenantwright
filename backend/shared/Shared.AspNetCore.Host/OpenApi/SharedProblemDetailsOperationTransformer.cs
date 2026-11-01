using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Shared.AspNetCore.ProblemDetails;

namespace Shared.AspNetCore.Host.OpenApi;

internal sealed class SharedProblemDetailsOperationTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (operation.Responses?.ContainsKey("default") == true)
        {
            return;
        }

        var schema = await context.GetOrCreateSchemaAsync(
            typeof(SharedHttpProblemDetails),
            parameterDescription: null,
            cancellationToken);

        operation.Responses ??= new OpenApiResponses();
        operation.Responses["default"] = new OpenApiResponse
        {
            Description = "RFC 7807 error response.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                [SharedProblemDetailsFactory.ContentType] = new()
                {
                    Schema = schema
                }
            }
        };
    }
}
