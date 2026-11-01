using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Shared.AspNetCore.Host.OpenApi;

/// <summary>
/// Publishes an operation's <see cref="SortableFieldsAttribute"/> declaration into the OpenAPI document as
/// <c>x-tenantwright-sortable-fields</c>, with the declared default as <c>x-tenantwright-default-sort</c>.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the sortable contract is enforced by <see cref="SortingValidator"/> at runtime but is
/// invisible to any generated client, which leaves a frontend grid with two options: hardcode a copy of each
/// endpoint's sortable set, or advertise every column and let the server reject some of them. The first drifts
/// silently the moment a field is added or renamed; the second shows an operator a sort that returns a 400.
/// Emitting the declaration makes the whitelist a single source both sides read.
/// </para>
/// <para>
/// Only the sortable set is published, never a mapping to columns — field names are a contract token, and the
/// database shape they order by stays an implementation detail.
/// </para>
/// </remarks>
internal sealed class SharedSortableFieldsOperationTransformer : IOpenApiOperationTransformer
{
    private const string FieldsExtensionName = "x-tenantwright-sortable-fields";
    private const string DefaultSortExtensionName = "x-tenantwright-default-sort";
    private const string MaxClausesExtensionName = "x-tenantwright-max-sort-clauses";

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var declaration = FindDeclaration(context);
        if (declaration is null)
        {
            return Task.CompletedTask;
        }

        var fields = new JsonArray();
        foreach (var field in declaration.Fields)
        {
            fields.Add(field);
        }

        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        operation.Extensions[FieldsExtensionName] = new JsonNodeExtension(fields);
        operation.Extensions[MaxClausesExtensionName] = new JsonNodeExtension(declaration.MaxClauses);

        if (declaration.DefaultSort is not null)
        {
            operation.Extensions[DefaultSortExtensionName] = new JsonNodeExtension(declaration.DefaultSort);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the declaration from the action's own parameter types.
    /// </summary>
    /// <remarks>
    /// Read from the action descriptor rather than <c>ParameterDescriptions</c> because a complex
    /// <c>[FromQuery]</c> input is flattened into one description per property, which loses the containing
    /// type the attribute is declared on.
    ///
    /// Only one input per operation may declare a set. Two would mean two <c>Sorting</c> properties bound
    /// from the same query string, which is a contract defect rather than something to merge here.
    /// </remarks>
    private static SortableFieldsAttribute? FindDeclaration(OpenApiOperationTransformerContext context)
    {
        if (context.Description.ActionDescriptor is not ControllerActionDescriptor action)
        {
            return null;
        }

        return action.MethodInfo
            .GetParameters()
            .Select(parameter => SortingValidator.GetDeclaration(parameter.ParameterType))
            .FirstOrDefault(candidate => candidate is not null);
    }
}
