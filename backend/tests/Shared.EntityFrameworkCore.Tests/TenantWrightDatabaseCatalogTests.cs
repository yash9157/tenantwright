using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Data.ConnectionStrings;
using Shared.MultiTenancy;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

/// <summary>
/// Every service DbContext names its own catalog database, and only tenant-aware contexts whose tenant-owned rows are
/// all <c>IMultiTenant</c> may be moved to a dedicated tenant database.
/// </summary>
public class TenantWrightDatabaseCatalogTests
{
    [Theory]
    [InlineData(typeof(AuditLogsDbContextApp), TenantWrightDatabaseCatalog.AuditLogs, true)]
    [InlineData(typeof(NotificationsDbContextApp), TenantWrightDatabaseCatalog.Notifications, true)]
    [InlineData(typeof(PermissionsDbContextApp), TenantWrightDatabaseCatalog.Permissions, true)]
    [InlineData(typeof(IdentityDbContextApp), TenantWrightDatabaseCatalog.Identity, false)]
    [InlineData(typeof(TenantDbContextApp), TenantWrightDatabaseCatalog.Tenant, false)]
    public void Service_db_contexts_use_their_catalog_name(Type dbContextType, string expectedName, bool dedicatable)
    {
        Assert.Equal(expectedName, ConnectionStringNameAttribute.GetConnStringName(dbContextType));
        Assert.Equal(dedicatable, TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(expectedName));
        if (dbContextType.IsDefined(typeof(IgnoreMultiTenancyAttribute), inherit: false))
        {
            Assert.False(dedicatable);
        }
    }

    [Fact]
    public void Catalog_is_closed_ordinal_and_has_no_tenant_default()
    {
        Assert.Equal(TenantWrightDatabaseCatalog.All.Order(StringComparer.Ordinal), TenantWrightDatabaseCatalog.All);
        Assert.Equal(TenantWrightDatabaseCatalog.All.Count, TenantWrightDatabaseCatalog.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(TenantWrightDatabaseCatalog.TenantDedicatable, name => Assert.True(TenantWrightDatabaseCatalog.IsKnown(name)));
        Assert.False(TenantWrightDatabaseCatalog.IsKnown(Shared.Data.ConnectionStrings.ConnectionStrings.DefaultConnectionStringName));
        Assert.False(TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(Shared.Data.ConnectionStrings.ConnectionStrings.DefaultConnectionStringName));
        Assert.False(TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase("notifications"));
        Assert.False(TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(null));
    }
}
