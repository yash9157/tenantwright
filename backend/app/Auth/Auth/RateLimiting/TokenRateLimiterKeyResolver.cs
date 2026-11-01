using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Auth.RateLimiting;

public readonly record struct TokenRateLimitRequestInfo(
    string PartitionKey,
    string? ClientId,
    string? GrantType,
    string RemoteIpAddress);

public static class TokenRateLimiterKeyResolver
{
    public static TokenRateLimitingOptions Normalize(TokenRateLimitingOptions options)
    {
        return new TokenRateLimitingOptions
        {
            PermitLimit = Math.Max(1, options.PermitLimit),
            WindowSeconds = Math.Max(1, options.WindowSeconds),
            QueueLimit = Math.Max(0, options.QueueLimit),
            PartitionMode = options.PartitionMode
        };
    }

    public static TokenRateLimitRequestInfo ResolveForPartition(HttpContext httpContext, TokenRateLimitingOptions options)
    {
        var normalized = Normalize(options);

        var (partitionKey, clientId, remoteIp) = BuildPartition(httpContext, normalized);
        return new TokenRateLimitRequestInfo(partitionKey, clientId, null, remoteIp);
    }

    public static TokenRateLimitRequestInfo ResolveForRejection(HttpContext httpContext, TokenRateLimitingOptions options)
    {
        var normalized = Normalize(options);
        var (partitionKey, clientId, remoteIp) = BuildPartition(httpContext, normalized);
        var grantType = ResolveGrantType(httpContext.Request);
        return new TokenRateLimitRequestInfo(partitionKey, clientId, grantType, remoteIp);
    }

    public static TokenRateLimitRequestInfo Resolve(HttpContext httpContext, TokenRateLimitingOptions options)
    {
        return ResolveForRejection(httpContext, options);
    }

    public static string HashClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return "none";
        }

        return HashValue(clientId);
    }

    public static string HashPartitionKey(string partitionKey)
    {
        if (string.IsNullOrWhiteSpace(partitionKey))
        {
            return "none";
        }

        return HashValue(partitionKey);
    }

    private static string? ResolveClientId(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Authorization", out StringValues authorizationValues))
        {
            foreach (var rawHeader in authorizationValues)
            {
                if (!string.IsNullOrWhiteSpace(rawHeader) &&
                    TryParseBasicClientId(rawHeader, out var basicClientId))
                {
                    return basicClientId;
                }
            }
        }

        if (request.Headers.TryGetValue("X-Client-Id", out var clientIdHeader))
        {
            var headerValue = clientIdHeader.ToString();
            if (!string.IsNullOrWhiteSpace(headerValue))
            {
                return headerValue.Trim();
            }
        }

        var directClientId = ReadFormField(request, "client_id");
        if (!string.IsNullOrWhiteSpace(directClientId))
        {
            return directClientId.Trim();
        }

        return null;
    }

    private static string? ResolveGrantType(HttpRequest request)
    {
        var grantType = ReadFormField(request, "grant_type");
        if (string.IsNullOrWhiteSpace(grantType))
        {
            return null;
        }

        return grantType.Trim();
    }

    private static string? ReadFormField(HttpRequest request, string key)
    {
        try
        {
            if (!request.HasFormContentType)
            {
                return null;
            }

            if (!request.Form.TryGetValue(key, out var value))
            {
                return null;
            }

            return value.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseBasicClientId(string headerValue, out string? clientId)
    {
        clientId = null;
        if (string.IsNullOrWhiteSpace(headerValue) ||
            !headerValue.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var encoded = headerValue.Substring("Basic ".Length).Trim();
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(encoded);
            var value = Encoding.UTF8.GetString(bytes);
            var separatorIndex = value.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                return false;
            }

            var parsedClientId = value[..separatorIndex].Trim();
            if (string.IsNullOrWhiteSpace(parsedClientId))
            {
                return false;
            }

            clientId = parsedClientId;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string HashValue(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes[..6]).ToLowerInvariant();
    }

    private static (string PartitionKey, string? ClientId, string RemoteIpAddress) BuildPartition(
        HttpContext httpContext,
        TokenRateLimitingOptions options)
    {
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
        if (string.IsNullOrWhiteSpace(remoteIp))
        {
            remoteIp = "unknown";
        }

        string? clientId = null;
        if (options.PartitionMode == TokenRateLimitPartitionMode.ClientIp)
        {
            clientId = ResolveClientId(httpContext.Request);
        }

        var partitionKey = !string.IsNullOrWhiteSpace(clientId)
            ? $"{clientId}|{remoteIp}"
            : remoteIp;

        return (partitionKey, clientId, remoteIp);
    }
}
