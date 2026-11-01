using System.Globalization;
using System.Linq;

namespace Auth.Pages.Account;

internal static class AuthInputConstraints
{
    public const int TenantMaxLength = 64;
    public const int LoginInputMaxLength = 256;
    public const int PasswordMaxLength = 256;
    public const int DeviceFingerprintMaxLength = 128;
    public const int OtpCodeMaxLength = 12;
    public const int RecoveryCodeMaxLength = 128;
    public const int TokenMaxLength = 2048;

    public static string NormalizeTenant(string? value)
        => NormalizeRequired(value, TenantMaxLength);

    public static string NormalizeLoginInput(string? value)
        => NormalizeRequired(value, LoginInputMaxLength);

    public static string NormalizePassword(string? value)
        => NormalizeRequired(value, PasswordMaxLength);

    public static string? NormalizeDeviceFingerprint(string? value)
        => NormalizeOptional(value, DeviceFingerprintMaxLength);

    public static string? NormalizeOtp(string? value)
    {
        var normalized = NormalizeOptional(value, OtpCodeMaxLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return new string(normalized.Where(char.IsDigit).ToArray());
    }

    public static string? NormalizeRecoveryCode(string? value)
    {
        var normalized = NormalizeOptional(value, RecoveryCodeMaxLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpper(CultureInfo.InvariantCulture);
    }

    public static string NormalizeToken(string? value)
        => NormalizeRequired(value, TokenMaxLength);

    public static string NormalizeRequired(string? value, int maxLength)
        => NormalizeOptional(value, maxLength) ?? string.Empty;

    public static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength];
    }
}
