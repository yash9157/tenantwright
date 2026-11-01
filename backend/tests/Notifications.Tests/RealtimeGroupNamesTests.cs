using Notifications.Application.Contracts.Realtime;
using Xunit;

namespace Notifications.Tests;

public class RealtimeGroupNamesTests
{
    [Fact]
    public void UserGroup_host_builds_expected_key()
    {
        var group = NotificationsRealtimeGroupNames.UserGroup(null, "u1");
        Assert.Equal("host:user:u1", group);
    }

    [Fact]
    public void UserGroup_tenant_builds_expected_key()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var group = NotificationsRealtimeGroupNames.UserGroup(tenantId, "u1");
        Assert.Equal("tenant:11111111-1111-1111-1111-111111111111:user:u1", group);
    }

    [Fact]
    public void PresenceKey_tenant_builds_expected_key()
    {
        var tenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var key = NotificationsRealtimeGroupNames.PresenceKey(tenantId, "u2");
        Assert.Equal("presence:tenant:22222222-2222-2222-2222-222222222222:user:u2", key);
    }
}
