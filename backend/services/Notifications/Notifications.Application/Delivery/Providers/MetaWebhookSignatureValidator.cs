using System.Security.Cryptography;
using System.Text;

namespace Notifications.Application.Delivery.Providers;

/// <summary>
/// Validates Meta's <c>X-Hub-Signature-256</c> header: <c>sha256=</c> followed by the lowercase hex
/// HMAC-SHA256 of the raw request body, keyed by the Meta App Secret.
/// </summary>
public static class MetaWebhookSignatureValidator
{
    private const string SignaturePrefix = "sha256=";

    public static bool IsValid(string appSecret, byte[] rawBody, string? providedSignatureHeader)
    {
        if (string.IsNullOrEmpty(appSecret) ||
            string.IsNullOrEmpty(providedSignatureHeader) ||
            !providedSignatureHeader.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var providedHex = providedSignatureHeader[SignaturePrefix.Length..];
        var providedBytes = TryDecodeHex(providedHex);
        if (providedBytes == null)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var expected = hmac.ComputeHash(rawBody);

        return CryptographicOperations.FixedTimeEquals(expected, providedBytes);
    }

    private static byte[]? TryDecodeHex(string value)
    {
        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
