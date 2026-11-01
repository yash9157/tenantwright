namespace Shared.Permissions.Caching;

public static class PermissionCacheKeys
{
    // Bump this when the serialized permission cache shape or format changes.
    private const string Prefix = "perm:v3";

    public static string BuildProviderKey(Guid? tenantId, string providerName, string providerKey)
    {
        var tenantPart = tenantId.HasValue ? tenantId.Value.ToString("D") : "host";
        return $"{Prefix}:t:{tenantPart}:pn:{providerName.Trim()}:pk:{Hash(providerKey)}";
    }

    public static string BuildProviderGrantsKey(Guid? tenantId, string providerName, string providerKey, long stamp)
    {
        return $"{BuildProviderKey(tenantId, providerName, providerKey)}:s:{stamp}";
    }

    public static string BuildTenantFeatureKey(Guid? tenantId, string featureName)
    {
        var tenantPart = tenantId.HasValue ? tenantId.Value.ToString("D") : "host";
        return $"{Prefix}:t:{tenantPart}:f:{featureName.Trim()}";
    }

    public static string BuildSnapshotKey(Guid? tenantId, Guid userId, string rolesHash, long stamp)
    {
        var tenantPart = tenantId.HasValue ? tenantId.Value.ToString("D") : "host";
        return $"{Prefix}:t:{tenantPart}:u:{Hash(userId.ToString("D"))}:r:{rolesHash}:s:{stamp}";
    }

    public static string BuildDecisionKey(Guid? tenantId, Guid userId, string rolesHash, long stamp, string permissionName)
    {
        var tenantPart = tenantId.HasValue ? tenantId.Value.ToString("D") : "host";
        return $"{Prefix}:t:{tenantPart}:u:{Hash(userId.ToString("D"))}:r:{rolesHash}:s:{stamp}:p:{Hash(permissionName)}";
    }

    private static string Hash(string value)
    {
        value = value.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return "0";
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
