using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Auth.Pages.Account.Validation;

public sealed class EmailOrUsernameAttribute : ValidationAttribute
{
    private static readonly EmailAddressAttribute EmailValidator = new();

    private const string UsernamePattern = @"^[A-Za-z0-9._-]+$";

    public EmailOrUsernameAttribute()
    {
        ErrorMessage = "Enter a valid email or username.";
    }

    public override bool IsValid(object? value)
    {
        if (value is not string raw || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var normalized = raw.Trim();
        if (normalized.Contains('@', StringComparison.Ordinal))
        {
            return EmailValidator.IsValid(normalized);
        }

        return Regex.IsMatch(
            normalized,
            UsernamePattern,
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
    }
}
