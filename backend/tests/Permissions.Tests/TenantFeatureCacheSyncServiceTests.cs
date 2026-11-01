using Permissions.Application.Caching;
using Xunit;

namespace Permissions.Tests;

public sealed class TenantFeatureCacheSyncServiceTests
{
    [Fact]
    public void Resolve_feature_value_prefers_explicit_value()
    {
        var value = TenantFeatureCacheSyncService.ResolveFeatureValue(
            "Notifications.Email",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Notifications.Email"] = "false" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Notifications.Email"] = "true" });

        Assert.Equal("false", value);
    }

    [Fact]
    public void Resolve_feature_value_uses_definition_default_when_explicit_value_is_missing()
    {
        var value = TenantFeatureCacheSyncService.ResolveFeatureValue(
            "Notifications.Email",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Notifications.Email"] = "true" });

        Assert.Equal("true", value);
    }

    [Fact]
    public void Resolve_feature_value_fails_closed_when_no_explicit_or_default_value_exists()
    {
        var value = TenantFeatureCacheSyncService.ResolveFeatureValue(
            "Notifications.Unknown",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal("false", value);
    }
}
