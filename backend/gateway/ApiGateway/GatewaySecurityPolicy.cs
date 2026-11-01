using System.Text;
using Microsoft.AspNetCore.Http;

namespace ApiGateway;

public static class GatewaySecurityPolicy
{
    private static readonly string[] UntrustedHeaders =
    [
        "X-Tenant-Id",
        "X-Internal-Request",
        "X-Internal-Client",
        "X-Client-Certificate",
        "X-Forwarded-Client-Cert"
    ];

    public static bool IsBlockedPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim('/');
        var firstSlash = normalized.IndexOf('/', StringComparison.Ordinal);
        if (firstSlash <= 1)
        {
            return false;
        }

        var gatewayPrefix = normalized[..firstSlash];
        if (gatewayPrefix.Length < 2 || (gatewayPrefix[0] != 'g' && gatewayPrefix[0] != 'G'))
        {
            return false;
        }

        for (var i = 1; i < gatewayPrefix.Length; i++)
        {
            if (!char.IsDigit(gatewayPrefix[i]))
            {
                return false;
            }
        }

        var remainder = normalized[(firstSlash + 1)..];
        var secondSlash = remainder.IndexOf('/', StringComparison.Ordinal);
        var firstSegmentAfterGateway = secondSlash >= 0 ? remainder[..secondSlash] : remainder;

        return string.Equals(firstSegmentAfterGateway, "internal", StringComparison.OrdinalIgnoreCase)
               || string.Equals(firstSegmentAfterGateway, "integration-api", StringComparison.OrdinalIgnoreCase);
    }

    public static void RemoveUntrustedHeaders(IHeaderDictionary headers)
    {
        foreach (var header in UntrustedHeaders)
        {
            headers.Remove(header);
        }
    }

    public static bool TryPromoteNotificationsHubAccessToken(
        HttpRequest request)
    {
        if (!request.Path.StartsWithSegments("/g5/hubs/notifications", StringComparison.Ordinal) ||
            request.Headers.ContainsKey("Authorization") ||
            !request.Query.TryGetValue("access_token", out var accessTokens) ||
            accessTokens.Count != 1 ||
            string.IsNullOrWhiteSpace(accessTokens[0]))
        {
            return false;
        }

        request.Headers.Authorization = $"Bearer {accessTokens[0]}";
        request.QueryString = QueryString.Create(
            request.Query
                .Where(pair => !string.Equals(pair.Key, "access_token", StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value.Select(value =>
                    new KeyValuePair<string, string?>(pair.Key, value))));
        return true;
    }

    public static bool HasAuthorizationHeaderWithinLimit(IHeaderDictionary headers, int maxFieldBytes)
    {
        if (!headers.TryGetValue("Authorization", out var authorizationValues))
        {
            return true;
        }

        if (authorizationValues.Count != 1 || authorizationValues[0] is not { } authorizationValue)
        {
            return false;
        }

        // The field budget includes the header name, separator, value, and terminating CRLF.
        var fieldBytes = Encoding.UTF8.GetByteCount("Authorization") + 2 +
                         Encoding.UTF8.GetByteCount(authorizationValue) + 2;
        return fieldBytes <= maxFieldBytes;
    }
}
