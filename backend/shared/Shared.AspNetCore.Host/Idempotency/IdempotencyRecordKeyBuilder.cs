using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Shared.MultiTenancy;

namespace Shared.AspNetCore.Host.Idempotency;

public static class IdempotencyRecordKeyBuilder
{
    public static async Task<string> BuildAsync(
        HttpContext httpContext,
        ICurrentTenant currentTenant,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var tenantMarker = currentTenant.Id?.ToString("D") ?? "host";
        var actor =
            FindClaimValue(httpContext.User, "sub")
            ?? FindClaimValue(httpContext.User, "client_id")
            ?? FindClaimValue(httpContext.User, ClaimTypes.NameIdentifier)
            ?? "anonymous";

        var request = httpContext.Request;
        var raw = $"{tenantMarker}|{actor}|{request.Method}|{request.Path.Value}|{idempotencyKey.Trim()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

        return $"api:idempotency:record:{hash}";
    }

    public static async Task<string> ComputeBodyFingerprintAsync(HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Body == Stream.Null)
        {
            return "empty";
        }

        request.EnableBuffering();
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        using var sha256 = SHA256.Create();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            sha256.TransformBlock(buffer, 0, read, null, 0);
        }

        sha256.TransformFinalBlock([], 0, 0);

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    private static string? FindClaimValue(ClaimsPrincipal user, string claimType)
    {
        var claim = user.FindFirst(claimType);
        return claim?.Value;
    }
}
