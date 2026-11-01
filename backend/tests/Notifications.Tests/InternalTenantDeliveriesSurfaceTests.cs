using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Controllers.InternalTenantDeliveries;
using Xunit;

namespace Notifications.Tests;

public sealed class InternalTenantDeliveriesSurfaceTests
{
    [Fact]
    public void Pending_delivery_count_is_an_m2m_only_get_surface()
    {
        var controller = typeof(InternalTenantDeliveriesController);
        var policy = Assert.Single(controller.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>());
        Assert.Equal("NotificationsInternal", policy.Policy);

        var method = controller.GetMethod(nameof(InternalTenantDeliveriesController.GetPendingCountAsync));
        Assert.NotNull(method);
        var route = Assert.Single(method!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true).Cast<HttpGetAttribute>());
        Assert.Equal("pending-count", route.Template);
    }
}
