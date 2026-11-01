using MailKit.Security;

namespace Notifications.Application.Emailing;

internal static class EmailProviderConfigSmtpHelpers
{
    public static SecureSocketOptions ParseSecureSocketOptions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SecureSocketOptions.StartTls;
        }

        return Enum.TryParse<SecureSocketOptions>(value, ignoreCase: true, out var parsed)
            ? parsed
            : SecureSocketOptions.StartTls;
    }
}
