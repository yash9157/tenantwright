using Permissions.Application.AccessReviews;
using System.Reflection;
using Microsoft.Extensions.Caching.Hybrid;
using Permissions.Application.Caching;
using Permissions.Application.FeatureManagement;
using Shared.Permissions;
using Xunit;

namespace Permissions.Tests;

public sealed class PermissionCacheLifetimeTests
{
    [Theory]
    [MemberData(nameof(CacheOptionOwners))]
    public void Permission_cache_lifetimes_should_be_one_hour_or_less(Type ownerType, string fieldName)
    {
        var field = ownerType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var options = Assert.IsType<HybridCacheEntryOptions>(field!.GetValue(null));
        Assert.NotNull(options.Expiration);
        Assert.True(options.Expiration <= TimeSpan.FromHours(1), $"{ownerType.Name}.{fieldName} exceeds one hour.");
    }


    [Fact]
    public void Role_privilege_matrix_cache_lifetime_should_not_exceed_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), AccessReviewAppService.ComputeMatrixCacheExpiration(null));
    }

    [Fact]
    public void Role_privilege_matrix_cache_lifetime_should_cap_at_the_nearest_role_grant_expiry()
    {
        var expiration = AccessReviewAppService.ComputeMatrixCacheExpiration(DateTime.UtcNow.AddMinutes(3));

        Assert.True(expiration <= TimeSpan.FromMinutes(3));
        Assert.True(expiration > TimeSpan.FromMinutes(2));
    }

    public static IEnumerable<object[]> CacheOptionOwners()
    {
        yield return [typeof(FeatureCacheSeedService), "StampCacheOptions"];
        yield return [typeof(PermissionProviderGrantCacheSyncService), "CacheOptions"];
        yield return [typeof(PermissionStampService), "StampCacheOptions"];
        yield return [typeof(TenantFeatureCacheSyncService), "CacheOptions"];
        yield return [typeof(PermissionGrantChangedEventHandler), "StampCacheOptions"];
    }
}
