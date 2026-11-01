using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.ProviderConfig;

internal static class NotificationProviderConfigCacheKeys
{
    public static string Default(Guid? tenantId, NotificationChannel channel)
    {
        var tenantPart = tenantId.HasValue ? tenantId.Value.ToString("D") : "host";
        return $"notifications:provider-config:default:{channel}:{tenantPart}";
    }
}
