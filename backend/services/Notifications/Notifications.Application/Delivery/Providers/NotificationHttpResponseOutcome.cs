using System.Net;
using Notifications.Application.Contracts.Channels;

namespace Notifications.Application.Delivery.Providers;

internal static class NotificationHttpResponseOutcome
{
    public static string? NumericProviderCode(string? value) =>
        value is { Length: > 0 and <= 10 } && value.All(char.IsAsciiDigit) ? value : null;

    public static async Task<string?> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = 65536;
        if (content.Headers.ContentLength is > maximumBytes)
            return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maximumBytes + 1];
        var count = await stream.ReadAtLeastAsync(buffer.AsMemory(), buffer.Length, throwOnEndOfStream: false, cancellationToken);
        return count > maximumBytes ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, count);
    }

    public static NotificationProviderSendResult Rejection(HttpResponseMessage response, string code,
        TimeSpan? minimumBackoff = null, bool documentedServerRejection = false)
    {
        var retryAt = response.Headers.RetryAfter?.Date?.UtcDateTime;
        if (response.Headers.RetryAfter?.Delta is { } delay)
            retryAt = DateTime.UtcNow.Add(delay);
        var minimum = DateTime.UtcNow.Add(minimumBackoff ?? TimeSpan.FromSeconds(1));
        if (retryAt is null || retryAt < minimum)
            retryAt = minimum;
        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => NotificationProviderSendResult.RetryableRejection(code, retryAt),
            HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable when documentedServerRejection =>
                NotificationProviderSendResult.RetryableRejection(code, retryAt),
            HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or
                HttpStatusCode.MethodNotAllowed or HttpStatusCode.Gone or HttpStatusCode.RequestEntityTooLarge or
                HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity => NotificationProviderSendResult.PermanentRejection(code),
            _ => NotificationProviderSendResult.Ambiguous(code)
        };
    }
}
