using AppPermissionCacheKeys = Permissions.Application.Caching.PermissionCacheKeys;
using AppFeatureCacheKeys = Permissions.Application.Caching.FeatureCacheKeys;
using SharedPermissionCacheKeys = Shared.Permissions.Caching.PermissionCacheKeys;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionCacheKeyCompatibilityTests
{
    [Fact]
    public void Permission_service_writer_keys_match_shared_runtime_reader_keys()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var userId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        Assert.Equal(
            SharedPermissionCacheKeys.BuildProviderKey(null, "Role", "admin"),
            AppPermissionCacheKeys.BuildProviderKey(null, "Role", "admin"));
        Assert.Equal(
            SharedPermissionCacheKeys.BuildProviderKey(tenantId, "User", userId.ToString("D")),
            AppPermissionCacheKeys.BuildProviderKey(tenantId, "User", userId.ToString("D")));
        Assert.Equal(
            SharedPermissionCacheKeys.BuildProviderKey(null, "Feature", "host"),
            AppPermissionCacheKeys.BuildProviderKey(null, "Feature", "host"));
        Assert.Equal(
            SharedPermissionCacheKeys.BuildProviderKey(tenantId, "Feature", tenantId.ToString("D")),
            AppPermissionCacheKeys.BuildProviderKey(tenantId, "Feature", tenantId.ToString("D")));
        Assert.Equal(
            SharedPermissionCacheKeys.BuildTenantFeatureKey(null, "Notifications.Email"),
            AppFeatureCacheKeys.BuildTenantFeatureKey(null, "Notifications.Email"));
        Assert.Equal(
            SharedPermissionCacheKeys.BuildTenantFeatureKey(tenantId, "Notifications.Email"),
            AppFeatureCacheKeys.BuildTenantFeatureKey(tenantId, "Notifications.Email"));
        Assert.Equal(
            SharedPermissionCacheKeys.BuildProviderGrantsKey(null, "Role", "admin", 123),
            AppPermissionCacheKeys.BuildProviderGrantsKey(null, "Role", "admin", 123));
        Assert.Equal(
            SharedPermissionCacheKeys.BuildSnapshotKey(tenantId, userId, "roles", 123),
            AppPermissionCacheKeys.BuildSnapshotKey(tenantId, userId, "roles", 123));
    }
}
