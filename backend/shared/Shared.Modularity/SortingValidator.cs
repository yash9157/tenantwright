using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Shared;

/// <summary>
/// Validates a caller-supplied <c>Sorting</c> value against the <see cref="SortableFieldsAttribute"/>
/// declared on the input DTO.
/// </summary>
/// <remarks>
/// <para>
/// This replaces per-service hand-written whitelists (a <c>switch</c> per application service) with one
/// checked implementation. A string sort is safe precisely because it is checked here: nothing reaches a
/// query unless it matched a declared field, so there is no dynamic-ordering injection surface.
/// </para>
/// <para>
/// The validator owns the <em>contract</em> — which field names are accepted and in which direction.
/// The owning application service still owns the <em>query</em>, mapping an accepted name to its
/// <c>OrderBy</c> expression, because some sorts are not simple column reads (a last-login sort may
/// aggregate over another table). Keep the two in step: every declared field needs a mapping.
/// </para>
/// </remarks>
public static class SortingValidator
{
    private const string SortingParameterName = "sorting";

    // Reflection is resolved once per input type. List endpoints are a hot path and the attribute is
    // immutable, so re-reading it per request would be pure overhead.
    private static readonly ConcurrentDictionary<Type, SortableFieldsAttribute?> DeclarationCache = new();

    /// <summary>
    /// Returns the sortable-field declaration for <paramref name="inputType"/>, or null when the type
    /// declares none. Shared with the OpenAPI transformer and contract tests so the attribute is read
    /// through one cached path.
    /// </summary>
    public static SortableFieldsAttribute? GetDeclaration(Type inputType)
    {
        ArgumentNullException.ThrowIfNull(inputType);
        return DeclarationCache.GetOrAdd(
            inputType,
            static type => type.GetCustomAttribute<SortableFieldsAttribute>(inherit: true));
    }

    /// <inheritdoc cref="GetDeclaration(Type)"/>
    public static SortableFieldsAttribute? GetDeclaration<TInput>() => GetDeclaration(typeof(TInput));

    /// <summary>
    /// Validates <paramref name="sorting"/> and returns it, or the declared default when the caller sent
    /// nothing. Returns null when the type declares no sortable fields and the caller asked for none.
    /// </summary>
    /// <exception cref="SharedValidationException">
    /// The type declares no sortable fields but a sort was requested, or a requested field or direction
    /// is not accepted. Carries a <c>sorting</c> field error so a client can attach it to the control
    /// that produced it rather than showing an unattributed message.
    /// </exception>
    public static string? ValidateAndNormalize<TInput>(string? sorting) =>
        ValidateAndNormalize(typeof(TInput), sorting);

    /// <inheritdoc cref="ValidateAndNormalize{TInput}(string?)"/>
    public static string? ValidateAndNormalize(Type inputType, string? sorting)
    {
        ArgumentNullException.ThrowIfNull(inputType);

        var declaration = GetDeclaration(inputType);
        var requested = sorting?.Trim();

        if (declaration is null)
        {
            if (string.IsNullOrEmpty(requested))
            {
                return null;
            }

            throw Invalid(
                $"This request does not support sorting.",
                Array.Empty<string>());
        }

        if (string.IsNullOrEmpty(requested))
        {
            return declaration.DefaultSort;
        }

        var allowed = new HashSet<string>(declaration.Fields, StringComparer.OrdinalIgnoreCase);
        var clauses = requested.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (clauses.Length == 0)
        {
            return declaration.DefaultSort;
        }

        // Rejected rather than truncated: an application service that cannot compose this many clauses
        // would otherwise fall back to its default order, answering a different question than the one
        // the caller asked.
        if (clauses.Length > declaration.MaxClauses)
        {
            throw Invalid(
                declaration.MaxClauses == 1
                    ? "Only one sort clause is supported."
                    : $"At most {declaration.MaxClauses} sort clauses are supported.",
                declaration.Fields);
        }

        var normalized = new List<string>(clauses.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var clause in clauses)
        {
            var parts = clause.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length is < 1 or > 2)
            {
                throw Invalid(
                    $"'{clause}' is not a valid sort clause. Use \"Field\" or \"Field desc\".",
                    declaration.Fields);
            }

            if (!allowed.TryGetValue(parts[0], out var canonicalField))
            {
                throw Invalid(
                    $"'{parts[0]}' is not a sortable field.",
                    declaration.Fields);
            }

            // Repeating a field cannot change the result and usually signals a client bug, so it is
            // rejected rather than silently collapsed.
            if (!seen.Add(canonicalField))
            {
                throw Invalid(
                    $"Sortable field '{canonicalField}' is repeated.",
                    declaration.Fields);
            }

            if (parts.Length == 1)
            {
                normalized.Add(canonicalField);
                continue;
            }

            var descending = parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
            if (!descending && !parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(
                    $"'{parts[1]}' is not a valid sort direction. Use 'asc' or 'desc'.",
                    declaration.Fields);
            }

            // An explicit direction is preserved rather than collapsed, including "asc". Some existing
            // contracts give a bare field name a descending default — Identity's `created` and
            // `lastlogin` both order newest-first — so dropping a caller's explicit "asc" would silently
            // invert their sort.
            normalized.Add($"{canonicalField} {(descending ? "desc" : "asc")}");
        }

        return string.Join(", ", normalized);
    }

    private static SharedValidationException Invalid(string message, IReadOnlyList<string> allowedFields)
    {
        var detail = allowedFields.Count == 0
            ? message
            : $"{message} Sortable fields: {string.Join(", ", allowedFields)}.";

        return new SharedValidationException(
            detail,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [SortingParameterName] = new[] { detail },
            });
    }
}
