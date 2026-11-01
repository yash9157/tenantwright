namespace Notifications.Application.Contracts.Realtime;

public static class NotificationsRealtimeGroupNames
{
    public static string UserGroup(Guid? tenantId, string userId)
    {
        userId = userId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("userId is required.", nameof(userId));
        }

        return tenantId.HasValue
            ? $"tenant:{tenantId.Value:D}:user:{userId}"
            : $"host:user:{userId}";
    }

    public static string PresenceKey(Guid? tenantId, string userId)
    {
        userId = userId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("userId is required.", nameof(userId));
        }

        return tenantId.HasValue
            ? $"presence:tenant:{tenantId.Value:D}:user:{userId}"
            : $"presence:host:user:{userId}";
    }
}
