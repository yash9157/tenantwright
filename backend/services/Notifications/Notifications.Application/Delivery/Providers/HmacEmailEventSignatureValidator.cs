using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery.Providers;

/// <summary>
/// Generic HMAC-SHA256 webhook signature validator. Computes <c>HMAC(secret, "{timestamp}.{body}")</c>
/// (or <c>HMAC(secret, body)</c> when no timestamp header is configured/present) and compares it,
/// in constant time, to the hex signature header. When a timestamp header is present it also enforces
/// the configured freshness tolerance (replay defense). Many transactional-email providers fit this
/// scheme via configuration; a provider that signs differently gets its own validator.
/// </summary>
public sealed class HmacEmailEventSignatureValidator : IEmailEventSignatureValidator, ITransientDependency
{
    private readonly NotificationsEmailFeedbackOptions _options;

    public HmacEmailEventSignatureValidator(IOptions<NotificationsEmailFeedbackOptions> options)
    {
        _options = options.Value;
    }

    public bool IsValid(EmailWebhookRequest request)
    {
        if (string.IsNullOrWhiteSpace(_options.SigningSecret))
        {
            return false;
        }

        if (!TryGetHeader(request, _options.SignatureHeader, out var providedSignature) ||
            string.IsNullOrWhiteSpace(providedSignature))
        {
            return false;
        }

        string? timestamp = null;
        if (!string.IsNullOrWhiteSpace(_options.TimestampHeader) &&
            TryGetHeader(request, _options.TimestampHeader, out var ts) &&
            !string.IsNullOrWhiteSpace(ts))
        {
            timestamp = ts;
            if (!IsTimestampFresh(ts))
            {
                return false;
            }
        }

        var signedPayload = timestamp is null ? request.RawBody : $"{timestamp}.{request.RawBody}";
        var expected = ComputeHmacHex(_options.SigningSecret!, signedPayload);

        return FixedTimeEqualsHex(expected, providedSignature.Trim());
    }

    private bool IsTimestampFresh(string timestamp)
    {
        DateTimeOffset signedAt;
        if (long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        else if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out signedAt))
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - signedAt;
        return Math.Abs(age.TotalSeconds) <= Math.Max(1, _options.TimestampToleranceSeconds);
    }

    private static string ComputeHmacHex(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static bool FixedTimeEqualsHex(string expectedHex, string providedHex)
    {
        byte[] expected;
        byte[] provided;
        try
        {
            expected = Convert.FromHexString(expectedHex);
            provided = Convert.FromHexString(providedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private static bool TryGetHeader(EmailWebhookRequest request, string name, out string? value)
    {
        foreach (var pair in request.Headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
