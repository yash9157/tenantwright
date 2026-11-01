using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Shared;

/// <summary>
/// Declares the field names a paged/list input DTO accepts in its <c>Sorting</c> property.
/// </summary>
/// <remarks>
/// <para>
/// One declaration serves three consumers, so the sortable contract is stated once:
/// <list type="bullet">
///   <item><see cref="SortingValidator"/> rejects any sort outside the list, fail-closed.</item>
///   <item>The OpenAPI operation transformer publishes it as <c>x-tenantwright-sortable-fields</c>.</item>
///   <item>The generated frontend proxy turns that into a typed constant the grid uses to decide
///   which columns may advertise themselves as sortable.</item>
/// </list>
/// </para>
/// <para>
/// Absence of this attribute means the endpoint does not support sorting. That is deliberate: a
/// permissive default would let a caller order by an unindexed or unintended column, and a UI would
/// advertise a sort the backend cannot honour — which is exactly the class of defect where the grid
/// silently reorders one page instead of the whole result set.
/// </para>
/// <para>
/// Field names are the DTO/entity-facing tokens the owning application service maps to an
/// <c>OrderBy</c> expression (for example <c>"UserName"</c>, <c>"CreationTime"</c>). They are a
/// contract, not a database column: renaming one is a breaking API change.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class SortableFieldsAttribute : Attribute
{
    /// <summary>The exact set of field names this input accepts, in declaration order.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>
    /// Sort applied when the caller sends none, in the same <c>"Field"</c> or <c>"Field desc"</c>
    /// form as a caller-supplied value. Null lets the application service keep its own default.
    /// </summary>
    public string? DefaultSort { get; init; }

    /// <summary>
    /// How many comma-separated clauses a caller may combine. Defaults to one.
    /// </summary>
    /// <remarks>
    /// An application service that maps sorts with a <c>switch</c> over the whole sorting string can
    /// only honour a single clause. Accepting more would let <c>"Email, UserName"</c> pass validation
    /// and then fall through to the default order — a silent wrong answer rather than a rejected
    /// request. Raise this only for a service whose query mapping genuinely composes clauses.
    /// </remarks>
    public int MaxClauses
    {
        get => _maxClauses;
        init => _maxClauses = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(MaxClauses),
                value,
                "At least one sort clause must be permitted.");
    }

    private readonly int _maxClauses = 1;

    public SortableFieldsAttribute(params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var normalized = fields
            .Where(static field => !string.IsNullOrWhiteSpace(field))
            .Select(static field => field.Trim())
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "Declare at least one sortable field, or omit the attribute to declare the endpoint unsortable.",
                nameof(fields));
        }

        var duplicate = normalized
            .GroupBy(static field => field, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Sortable field '{duplicate.Key}' is declared more than once.",
                nameof(fields));
        }

        Fields = new ReadOnlyCollection<string>(normalized);
    }
}
