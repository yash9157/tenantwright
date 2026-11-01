using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Data.ConnectionStrings;
using Xunit;

namespace Permissions.Tests;

public class PermissionsDbContextConnectionStringNameTests
{
    [Fact]
    public void PermissionsDbContextApp_should_use_its_catalog_database_name()
    {
        var name = ConnectionStringNameAttribute.GetConnStringName<PermissionsDbContextApp>();

        Assert.Equal(TenantWrightDatabaseCatalog.Permissions, name);
        Assert.True(TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(name));
    }
}
