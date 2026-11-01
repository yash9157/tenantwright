using Shared.Permissions.Caching;
using Xunit;

namespace Shared.Permissions.Tests;

public sealed class PermissionCacheKeysTests
{
    [Fact]
    public void Permission_cache_keys_are_versioned()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var userId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        Assert.StartsWith("perm:v3:", PermissionCacheKeys.BuildProviderKey(null, "Role", "admin"), StringComparison.Ordinal);
        Assert.StartsWith("perm:v3:t:host:pn:Feature:", PermissionCacheKeys.BuildProviderKey(null, "Feature", "host"), StringComparison.Ordinal);
        Assert.StartsWith($"perm:v3:t:{tenantId:D}:pn:Feature:", PermissionCacheKeys.BuildProviderKey(tenantId, "Feature", tenantId.ToString("D")), StringComparison.Ordinal);
        Assert.StartsWith("perm:v3:", PermissionCacheKeys.BuildProviderGrantsKey(null, "Role", "admin", 123), StringComparison.Ordinal);
        Assert.Equal("perm:v3:t:host:f:Notifications.Config.Email", PermissionCacheKeys.BuildTenantFeatureKey(null, "Notifications.Config.Email"));
        Assert.StartsWith("perm:v3:", PermissionCacheKeys.BuildTenantFeatureKey(tenantId, "Notifications.Config.Email"), StringComparison.Ordinal);
        Assert.StartsWith("perm:v3:", PermissionCacheKeys.BuildSnapshotKey(tenantId, userId, "roles", 123), StringComparison.Ordinal);
        Assert.StartsWith("perm:v3:", PermissionCacheKeys.BuildDecisionKey(tenantId, userId, "roles", 123, "Notifications.Config.Email"), StringComparison.Ordinal);
    }
}
