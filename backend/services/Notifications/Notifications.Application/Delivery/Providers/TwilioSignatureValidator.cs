using System.Security.Cryptography;
using System.Text;

namespace Notifications.Application.Delivery.Providers;

/// <summary>
/// Validates Twilio's <c>X-Twilio-Signature</c> header. Twilio computes the signature as
/// Base64(HMAC-SHA1(authToken, fullUrl + concatenation of the POST parameters sorted by key, each
/// appended as key immediately followed by value)). See Twilio's request-validation spec.
/// </summary>
public static class TwilioSignatureValidator
{
    public static bool IsValid(
        string authToken,
        string requestUrl,
        IEnumerable<KeyValuePair<string, string>> formParameters,
        string? providedSignature)
    {
        if (string.IsNullOrEmpty(authToken) ||
            string.IsNullOrEmpty(requestUrl) ||
            string.IsNullOrEmpty(providedSignature))
        {
            return false;
        }

        var builder = new StringBuilder(requestUrl);
        foreach (var pair in formParameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append(pair.Value);
        }

        var expected = ComputeSignature(authToken, builder.ToString());
        var providedBytes = TryDecodeBase64(providedSignature);
        if (providedBytes == null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, providedBytes);
    }

    private static byte[] ComputeSignature(string authToken, string data)
    {
#pragma warning disable CA5350 // Twilio's request-validation protocol requires HMAC-SHA1; fixed-time comparison is enforced by the caller.
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
#pragma warning restore CA5350
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static byte[]? TryDecodeBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
