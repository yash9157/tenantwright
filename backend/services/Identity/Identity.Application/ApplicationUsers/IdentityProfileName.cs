using Shared;

namespace Identity.Application.ApplicationUsers;

/// <summary>
/// Single source of truth for normalizing and validating the optional
/// <c>Name</c>/<c>Surname</c> profile fields on <see cref="Identity.Domain.ApplicationUsers.ApplicationUser"/>.
/// Used by both self-service profile update and administrator-facing
/// user/platform-staff create/update so all paths behave identically:
/// whitespace-only input clears the value (maps to <c>null</c>), surrounding
/// whitespace is trimmed, and values longer than <see cref="MaxLength"/> are rejected.
/// </summary>
public static class IdentityProfileName
{
    /// <summary>
    /// Maximum stored length for a profile name component. Enforced at the
    /// application layer; the underlying column is unbounded <c>text</c>, so no
    /// migration is required and existing longer rows remain readable.
    /// </summary>
    public const int MaxLength = 128;

    /// <summary>
    /// Trims the value and maps empty/whitespace-only input to <c>null</c>.
    /// Throws <see cref="SharedValidationException"/> when the trimmed value
    /// exceeds <see cref="MaxLength"/>.
    /// </summary>
    /// <param name="value">Raw input value.</param>
    /// <param name="fieldName">Field label used in the validation message.</param>
    public static string? Normalize(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new SharedValidationException($"{fieldName} must be {MaxLength} characters or fewer.");
        }

        return trimmed;
    }
}
