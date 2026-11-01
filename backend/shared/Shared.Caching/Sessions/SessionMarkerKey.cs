namespace Shared.Caching.Sessions;

public static class SessionMarkerKey
{
    public static string Build(Guid? tenantId, Guid userId, string sessionId)
    {
        var tenantPart = tenantId?.ToString("D") ?? "host";
        var normalizedSessionId = NormalizeSessionId(sessionId);
        return $"sid:{tenantPart}:{userId:D}:{normalizedSessionId}";
    }

    public static string NormalizeSessionId(string sessionId)
    {
        if (Guid.TryParse(sessionId, out var parsed))
        {
            return parsed.ToString("D");
        }

        return sessionId.Trim();
    }
}
