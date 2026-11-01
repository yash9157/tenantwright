using System;

namespace Shared.Caching.Sessions;

/// <summary>
/// Single source of truth for the session-marker payload format shared by the
/// Identity login paths (which create the marker) and
/// <c>SessionValidationMiddleware</c> (which reads and refreshes it). The payload
/// encodes two UNIX-second instants, <c>absolute:idle</c>: the absolute session
/// expiry (login + <c>MarkerTtlHours</c>) and the current idle expiry
/// (last activity + <c>IdleTimeoutMinutes</c>). The Valkey key TTL is set to the
/// idle window so the key self-expires on inactivity; the absolute instant is a
/// hard cap that does not slide across refreshes.
/// </summary>
public static class SessionMarkerPayload
{
    public static string Format(DateTime absoluteExpiryUtc, DateTime idleExpiryUtc)
    {
        return $"{new DateTimeOffset(absoluteExpiryUtc).ToUnixTimeSeconds()}:{new DateTimeOffset(idleExpiryUtc).ToUnixTimeSeconds()}";
    }

    public static bool TryParse(string? value, out DateTime absoluteExpiryUtc, out DateTime idleExpiryUtc)
    {
        absoluteExpiryUtc = default;
        idleExpiryUtc = default;

        var parts = value?.Split(':');
        if (parts is not { Length: 2 } ||
            !long.TryParse(parts[0], out var absUnix) ||
            !long.TryParse(parts[1], out var idleUnix))
        {
            return false;
        }

        absoluteExpiryUtc = DateTimeOffset.FromUnixTimeSeconds(absUnix).UtcDateTime;
        idleExpiryUtc = DateTimeOffset.FromUnixTimeSeconds(idleUnix).UtcDateTime;
        return true;
    }

    /// <summary>
    /// Builds the initial marker created at login: absolute = now + marker TTL,
    /// idle = now + idle timeout, and the Valkey key TTL = the idle window.
    /// </summary>
    public static (string Payload, TimeSpan KeyTtl) CreateInitial(int markerTtlHours, int idleTimeoutMinutes, DateTime nowUtc)
    {
        var absoluteExpiry = nowUtc.AddHours(Math.Max(1, markerTtlHours));
        var idleExpiry = nowUtc.AddMinutes(Math.Max(1, idleTimeoutMinutes));
        return (Format(absoluteExpiry, idleExpiry), idleExpiry - nowUtc);
    }
}
