namespace Auth.Pages.Account.Validation;

internal static class PasswordValidationRules
{
    public static bool HasDigit(string password) => password.Any(char.IsDigit);

    public static bool HasLowercase(string password) => password.Any(char.IsLower);

    public static bool HasUppercase(string password) => password.Any(char.IsUpper);

    public static bool HasNonAlphanumeric(string password) => password.Any(ch => !char.IsLetterOrDigit(ch));
}
