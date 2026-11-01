using System;
using System.Text;

namespace Shared.AspNetCore.ProblemDetails;

/// <summary>
/// Normalizes a validation error key to the serialized property path a client actually binds.
/// </summary>
/// <remarks>
/// The keys in a validation <c>errors</c> dictionary reached the wire in three different shapes depending on
/// which path produced them, which made them unusable to a caller wanting to highlight the offending field:
/// <list type="bullet">
/// <item><description>
/// DataAnnotations failures arrive through <c>ModelState</c> as PascalCase CLR property paths —
/// <c>Reason</c>, <c>Contact.Email</c>, <c>Lines[0].NetAmountMinor</c>.
/// </description></item>
/// <item><description>
/// Body-deserialization failures arrive from <c>System.Text.Json</c> as JSON paths — <c>$.reason</c>.
/// </description></item>
/// <item><description>
/// Hand-thrown <see cref="Shared.SharedValidationException"/> keys are whatever the caller typed, and the
/// house convention there is already camelCase — <c>sorting</c>, <c>startDate</c>.
/// </description></item>
/// </list>
/// Responses serialize property names with <see cref="System.Text.Json.JsonSerializerDefaults.Web"/>, which
/// camelCases members but leaves dictionary keys alone — so a client received <c>Reason</c> for a field it
/// binds as <c>reason</c>. Normalizing here, at the one place each path is written, makes the key match the
/// serialized name in every case without asking each call site to remember.
/// </remarks>
public static class ValidationErrorKey
{
    /// <summary>
    /// Returns <paramref name="key"/> as a camelCase serialized property path, preserving separators and
    /// collection indexers. Returns the input unchanged when there is nothing meaningful to normalize, so an
    /// already-correct key is never mangled.
    /// </summary>
    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var trimmed = key.Trim();

        // System.Text.Json reports the offending member as a JSON path. The root marker means nothing to a
        // client matching against its own field names.
        if (trimmed.StartsWith("$.", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }
        else if (string.Equals(trimmed, "$", StringComparison.Ordinal))
        {
            // A whole-body failure names no field. Left as-is so the caller reports it rather than attaching
            // it to an arbitrary control.
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length);
        var atSegmentStart = true;

        foreach (var character in trimmed)
        {
            switch (character)
            {
                case '.':
                case '[':
                case ']':
                    builder.Append(character);
                    // A new segment begins after a separator, so its first letter is lowered too —
                    // `Contact.Email` has to become `contact.email`, not `contact.Email`.
                    atSegmentStart = character != ']';
                    break;
                default:
                    builder.Append(atSegmentStart ? char.ToLowerInvariant(character) : character);
                    atSegmentStart = false;
                    break;
            }
        }

        return builder.ToString();
    }
}
