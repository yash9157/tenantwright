namespace Permissions.Application.Caching;

public static class FeatureCacheKeys
{
    // Keep in sync with Shared.Permissions.Caching.PermissionCacheKeys.
    private const string Prefix = "perm:v3";

    public static string BuildTenantFeatureKey(Guid? tenantId, string featureName)
    {
        var tenantPart = tenantId.HasValue ? tenantId.Value.ToString("D") : "host";
        return $"{Prefix}:t:{tenantPart}:f:{featureName.Trim()}";
    }
}
